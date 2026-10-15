using Sandbox;

namespace Hl2rp;

/// <summary>
/// Lets a citizen move about the city on its own — drifting between nearby spots, pausing, and
/// drifting on. It keeps to a radius of where it started, so it wanders rather than emigrates.
///
/// <para><b>He is driven exactly as a player is.</b> Movement goes through the engine's
/// <c>PlayerController</c> by setting <c>WishVelocity</c>, the same door the player's own input
/// knock on. That matters for more than looks: it means the physics engine resolves where he may
/// go, so he cannot walk through a wall, and he picks up the engine's step handling, ground
/// detection and animation along with it. Moving the body by hand instead — setting its position
/// outright — carries him through anything in the way, which is exactly what it did.</para>
///
/// <para><b>Where it may go.</b> A destination is still chosen carefully: there must be floor under
/// it, so he does not set off toward a rooftop or off a ledge.</para>
/// </summary>
public sealed class CitizenWander : Component
{
	/// <summary>How far from the starting point it will stray, in units.</summary>
	[Property] public float Radius { get; set; } = 400f;

	/// <summary>How fast it walks. Deliberately below the player's walk, so a citizen ambles.</summary>
	[Property] public float Pace { get; set; } = 50f;

	/// <summary>Seconds it stands still between walks.</summary>
	[Property] public float PauseMin { get; set; } = 3f;
	[Property] public float PauseMax { get; set; } = 12f;

	/// <summary>How close counts as arrived.</summary>
	[Property] public float ArriveDistance { get; set; } = 32f;

	/// <summary>
	/// The longest a single walk may last, in seconds, before he gives up on it and stands a while.
	///
	/// <para>Without this a citizen only ever stopped on arrival, so any leg he could not finish —
	/// snagged on a kerb, stopped by a door, or simply a long way off at <see cref="Pace"/> — went on
	/// for ever. That is the "he runs forward and never stops" complaint: not that he would not stop,
	/// but that he had no way to.</para>
	/// </summary>
	[Property] public float LegTimeout { get; set; } = 20f;

	/// <summary>
	/// How long he has to be going nowhere before he gives up on getting there, in seconds. He is on
	/// his feet and wishing to walk but the body is not moving: something is in the way that the walk
	/// cannot solve. Standing a moment and choosing somewhere else is better than leaning on a wall
	/// until <see cref="LegTimeout"/> runs out.
	/// </summary>
	[Property] public float StuckSeconds { get; set; } = 1.5f;

	private Vector3 _home;
	private bool _haveHome;
	private Vector3 _target;
	private bool _walking;
	private TimeSince _sinceState;
	private Vector3 _lastWalkPosition;
	private float _stalled;

	private PlayerController? Controller => GetComponent<PlayerController>( true );

	/// <summary>
	/// Turns him to face a direction, the way the engine's own controller example does it: by moving
	/// its <c>EyeAngles</c> towards the heading, not by writing the transform.
	///
	/// <para>A pawn's facing belongs to its controller. Setting <c>WorldRotation</c> looks right for a
	/// frame and is then overwritten, so a citizen would travel one way while still looking another —
	/// which is what "running backwards" actually was.</para>
	/// </summary>
	private static void Face( PlayerController controller, Vector3 direction, float speed )
	{
		if ( direction.Length < 0.001f ) return;

		var target = direction.EulerAngles;
		controller.EyeAngles = Rotation.Slerp( controller.EyeAngles, target, Time.Delta * speed );
	}

	/// <summary>Where he started. Read by the citizen so he can speak about how far he has strayed —
	/// the difference between being on your own patch and being lost is one a person feels.</summary>
	public Vector3 HomePosition => _home;

	private CitizenNpc? Npc => GetComponent<CitizenNpc>( true );

	/// <summary>Where a named character is now, or null if they are not in the city. Unlike
	/// <see cref="Find"/> this changes nothing — it is only ever asked where somebody is, so it must
	/// not be allowed to cancel a follow order on its way past.</summary>
	private Vector3? WhereIs( string? name )
	{
		if ( string.IsNullOrEmpty( name ) ) return null;

		foreach ( var player in Scene.GetAllComponents<Hexagon.Player>() )
		{
			if ( player.HasCharacter && player.CharacterName?.Equals( name, System.StringComparison.OrdinalIgnoreCase ) == true )
				return player.WorldPosition;
		}

		return null;
	}

	/// <summary>Who the citizen is currently walking with, by character name — or null if they have
	/// gone out of the city since.</summary>
	private Vector3? Find( string name )
	{
		foreach ( var player in Scene.GetAllComponents<Hexagon.Player>() )
		{
			if ( player.HasCharacter && player.CharacterName?.Equals( name, System.StringComparison.OrdinalIgnoreCase ) == true )
				return player.WorldPosition;
		}

		// They are gone. There is nobody left to walk with.
		if ( Npc is not null ) Npc.Following = null;
		return null;
	}

	/// <summary>The name of whoever last spoke to him, so following can be held onto across frames.</summary>
	private string? WhoSpoke()
	{
		foreach ( var player in Scene.GetAllComponents<Hexagon.Player>() )
		{
			if ( player.HasCharacter ) return player.CharacterName;
		}

		return null;
	}

	/// <summary>How long to stand about this time, chosen fresh each time he stops.</summary>
	private float Pause => PauseMin + Game.Random.NextSingle() * System.MathF.Max( 0f, PauseMax - PauseMin );

	protected override void OnUpdate()
	{
		var controller = Controller;
		if ( controller is null ) return;

		// Insisted on every frame, not set once when he is made: a controller turns input back on
		// during its own initialisation, which runs after the line that would have disabled it. Left
		// unenforced, every citizen is steered by whoever is typing, and they all walk about exactly
		// as that person does.
		controller.UseInputControls = false;
		controller.UseCameraControls = false;
		controller.UseLookControls = false;

		// And the brake, insisted on here for the same reason. This one is not about steering but
		// about pace: a controller with no input to read believes it is always trying to stop, so at
		// the engine's default brake it slows the walk it is being asked to make to about a sixth of
		// it. <see cref="CitizenNpc.NpcBrakePower"/> has the measurements.
		controller.BrakePower = CitizenNpc.NpcBrakePower;

		// And if he is not on his feet, that is the end of his walking. Down, he is carried by a limp
		// body where he fell and this one has been hidden; dead, there is nothing of him to walk. He
		// stands still until somebody puts him back on his feet, at which point this stops being true
		// and he takes himself off again.
		if ( Npc is { Standing: false } )
		{
			controller.WishVelocity = Vector3.Zero;
			return;
		}

		// And gravity, for the same reason: a controller's body is made without it, and setting it
		// once at construction is undone when the controller initialises a moment later. Without it
		// the body has nothing holding it to the floor, and everyone drifts as though in water.
		if ( controller.Body.IsValid() )
			controller.Body.Gravity = true;

		// The host decides where he is going; every machine then simulates his body the same way it
		// simulates a player's, so the movement matches without being sent.
		if ( !_haveHome )
		{
			_home = WorldPosition;
			_haveHome = true;
			_sinceState = 0;
			return;
		}

		if ( !Networking.IsHost )
		{
			// A client just keeps the body upright; the host is the one choosing.
			return;
		}

		// Walking along with someone persists: it is not a step but a decision, and it holds until
		// they are told otherwise, or the person they are following is gone.
		if ( Npc is { Following: { } companion } && Find( companion ) is { } them )
		{
			var toThem = ( them - WorldPosition ).WithZ( 0f );

			// Close enough to walk beside them; do not crowd into their back.
			if ( toThem.Length < 110f )
			{
				controller.WishVelocity = Vector3.Zero;

				// Standing with them, turn to look their way rather than at whatever was behind.
				if ( toThem.Length > 1f )
					Face( controller, toThem.Normal, 2f );

				return;
			}

			// Left far behind: hurry, but not beyond a jog.
			var hurry = toThem.Length > 400f ? 1.8f : 1f;

			// Face the way she is going, through the controller and not the transform. A controller
			// owns where its pawn is looking; setting WorldRotation is overwritten the next frame,
			// which is why she travelled one way while still facing another — running backwards.
			Face( controller, toThem.Normal, 4f );

			controller.WishVelocity = toThem.Normal * Pace * hurry;
			return;
		}

		// What the model decided outranks wandering. If it told him to walk towards or away from
		// whoever spoke, that is the equivalent of a key being held, and he holds it.
		if ( Npc?.Urge is { } urge && Npc.SpeakingFrom is { } from )
		{
			switch ( urge )
			{
				case CitizenNpc.MoveUrge.Follow:
					Npc.Following = WhoSpoke();
					Npc.Urge = null;
					return;

				case CitizenNpc.MoveUrge.Stop:
					controller.WishVelocity = Vector3.Zero;
					Npc.Following = null;
					Npc.Urge = null;
					return;

				case CitizenNpc.MoveUrge.Toward:
				case CitizenNpc.MoveUrge.Away:
				{
					var toThem = ( from - WorldPosition ).WithZ( 0f );

					// Close enough, or too close: the walk is over.
					var done = urge == CitizenNpc.MoveUrge.Toward
						? toThem.Length < 90f
						: toThem.Length > 400f;

					if ( done )
					{
						controller.WishVelocity = Vector3.Zero;
						Npc.Urge = null;
						return;
					}

					var direction = urge == CitizenNpc.MoveUrge.Toward ? toThem.Normal : -toThem.Normal;
					Face( controller, direction, 4f );
					controller.WishVelocity = direction * Pace;
					return;
				}
			}
		}

		// Somebody is talking to him. He stops where he stands and turns to them, the way anyone
		// stops walking when they are spoken to, and stays that way for the whole exchange — that is,
		// until EngageSeconds after the last thing said to him. Everything above this is a decision
		// already taken (walking with somebody, or a movement the model asked for) and outranks it;
		// this only outranks wandering, which is what he does with himself when nobody wants him.
		if ( Npc is { Engaged: true } )
		{
			// Not merely paused mid-walk: the leg is dropped, so that when the conversation ends he
			// stands a moment and then chooses somewhere new, rather than setting off again the
			// instant the last word lands.
			_walking = false;
			_sinceState = 0;
			controller.WishVelocity = Vector3.Zero;

			// Face whoever is speaking, followed live so he keeps looking at them as they move
			// about, and falling back to where they were if they have since left the city.
			var themAt = WhereIs( Npc.AddressedBy ) ?? Npc.SpeakingFrom;
			if ( themAt is { } themWhere )
			{
				var look = ( themWhere - WorldPosition ).WithZ( 0f );
				if ( look.Length > 1f ) Face( controller, look.Normal, 4f );
			}

			return;
		}

		if ( !_walking )
		{
			controller.WishVelocity = Vector3.Zero;

			if ( _sinceState < Pause ) return;

			if ( TryPickTarget( out var next ) )
			{
				_target = next;
				_walking = true;
				// A fresh leg starts measuring its own progress from here, not from wherever the last
				// one ended.
				_lastWalkPosition = WorldPosition;
				_stalled = 0f;
			}

			_sinceState = 0;
			return;
		}

		var toTarget = ( _target - WorldPosition ).WithZ( 0f );

		// Is he getting anywhere? Measured over the body's own travel, not the wish, because a wish
		// says only what he wants. Walking into a wall is a wish of full strength and no movement, and
		// he should stop doing it in a second and a half rather than lean on it for twenty.
		var stepped = Vector3.DistanceBetween( _lastWalkPosition, WorldPosition );
		_lastWalkPosition = WorldPosition;
		_stalled = stepped < 0.02f ? _stalled + Time.Delta : 0f;

		// Arrived — or given up on ever arriving, or on getting anywhere at all. He must have all
		// three, or a leg he cannot finish never ends and he walks for ever, which is the "runs
		// forward and never stops" he was doing, or grinds against a wall, which is what he did next.
		if ( toTarget.Length < ArriveDistance || _sinceState > LegTimeout || _stalled > StuckSeconds )
		{
			_walking = false;
			controller.WishVelocity = Vector3.Zero;
			_sinceState = 0;
			_stalled = 0;
			return;
		}

		// Turn to face the way he is going. This call was missing on this path — it was only made
		// when following somebody or obeying an urge — so a wandering citizen kept the heading he
		// was built with and crossed the street sideways to his own model, whose animation carried on
		// running forwards all the while. That is why they looked like they were running forward
		// without going anywhere.
		Face( controller, toTarget.Normal, 4f );

		// Ask the engine to walk that way. It decides what that means around corners and walls.
		controller.WishVelocity = toTarget.Normal * Pace;
	}

	/// <summary>Looks for somewhere nearby he could actually stand: floor beneath, and not the
	/// void beyond an edge.</summary>
	private bool TryPickTarget( out Vector3 found )
	{
		var scene = Scene;

		for ( var attempt = 0; attempt < 12; attempt++ )
		{
			var angle = Game.Random.NextSingle() * System.MathF.PI * 2f;
			var distance = Game.Random.NextSingle() * Radius;
			var candidate = _home + new Vector3( System.MathF.Cos( angle ) * distance, System.MathF.Sin( angle ) * distance, 0f );

			var floor = scene.Trace.Ray( candidate + Vector3.Up * 64f, candidate - Vector3.Up * 256f )
				.WithoutTags( CitizenNpc.NpcTag, "player", "capsule" )
				.Run();

			if ( !floor.Hit ) continue;

			// And there has to be a way to walk there. A destination is not reachable merely because
			// it has floor under it: one sitting on the far side of a wall is exactly where he walked
			// into the wall and leaned on it, over and over, which is what "his movement is broken"
			// was the second time. The trace is at knee height, so a kerb he could step up is not a
			// wall, and it ignores everything loose, so a person or a crate in the way is not a wall
			// either — only something he genuinely cannot walk through.
			var way = scene.Trace.Ray( WorldPosition + Vector3.Up * 24f, floor.EndPosition + Vector3.Up * 24f )
				.IgnoreGameObjectHierarchy( GameObject )
				.WithoutTags( CitizenNpc.NpcTag, "player", "capsule", "corpse", "sleeper" )
				.IgnoreDynamic()
				.Run();

			// Unless he is already standing inside something, in which case the trace says nothing
			// about the way out and must not be allowed to rule every destination out.
			if ( way.Hit && !way.StartedSolid ) continue;

			found = floor.EndPosition;
			return true;
		}

		found = _home;
		return false;
	}
}
