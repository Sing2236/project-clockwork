using Sandbox;

namespace Hl2rp;

/// <summary>
/// Footsteps for characters in Combine factions, so a Civil Protection unit is heard coming.
///
/// <para><b>Why we do this by hand.</b> s&amp;box has a footstep system on <c>PlayerController</c>,
/// but it only makes a noise when the model's own animation graph raises a footstep event, and the
/// citizen the city is built from does not raise one on its own. Rather than re-author the
/// animgraph, this counts distance travelled and puts a step down every so many units, which is
/// both simpler and easier to tune.</para>
///
/// <para><b>Who simulates.</b> Movement belongs to the player who owns the pawn, so that is the
/// machine that counts the steps and tells everyone else to hear them. A proxy returns early: a
/// character you are merely watching must not run their feet on your machine as well as theirs.
/// Running strides are longer than walking ones, so the feet quicken when the legs do.</para>
/// </summary>
public sealed class CombineFootsteps : Component
{
	private float _travelled;
	private Vector3 _lastPosition;
	private bool _havePosition;

	private CombineVoice Settings => CombineVoice.Current!;

	private bool IsCombine()
	{
		if ( Settings is not { } settings ) return false;
		var faction = GetComponent<Hexagon.Player>( true )?.FactionPath;
		return !string.IsNullOrEmpty( faction ) && settings.Factions.Contains( faction );
	}

	protected override void OnUpdate()
	{
		// The owner drives its own feet; everyone else hears the result.
		if ( IsProxy ) return;
		if ( !IsCombine() ) return;

		var controller = GetComponent<PlayerController>( true );
		if ( controller is null ) return;

		// Distance actually covered, rather than the controller's Velocity. The engine documents
		// that as "our actual physical velocity minus our ground velocity", which reads back as
		// zero while running on solid ground — useless for counting strides. Where the pawn really
		// went cannot lie.
		var position = GameObject.WorldPosition;
		if ( !_havePosition )
		{
			_lastPosition = position;
			_havePosition = true;
			return;
		}

		var moved = (position - _lastPosition).WithZ( 0f ).Length;
		_lastPosition = position;

		// Standing still, or in the air, there is nothing to put down.
		if ( !controller.IsOnGround || moved < 0.01f )
		{
			_travelled = 0f;
			return;
		}

		// Only a run makes a noise. Someone walking past is meant to be quiet — the gear rattle is
		// for a unit moving with purpose, not for a stroll, and it keeps walking from becoming noise.
		var speed = moved / System.MathF.Max( Time.Delta, 1e-4f );
		var running = speed > controller.WalkSpeed * 1.1f;
		if ( !running )
		{
			_travelled = 0f;
			return;
		}

		_travelled += moved;

		// A longer stride at a run keeps the cadence right without needing a timer.
		var stride = Settings.RunStride;
		if ( _travelled < stride ) return;

		_travelled -= stride;
		PlayStep();
	}

	[Rpc.Broadcast( NetFlags.Unreliable )]
	private void PlayStep()
	{
		var sound = Settings?.StepSoundFor();

		// Temporary: proves a step reached the point of being heard, and on which machine.
		Log.Info( $"[footstep] step on {(Networking.IsHost ? "host" : "client")} at {GameObject.WorldPosition} sound={(sound is null ? "NULL" : "ok")}" );

		if ( sound is not null ) GameObject.PlaySound( sound );
	}
}
