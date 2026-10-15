using Sandbox;

namespace Hl2rp;

/// <summary>
/// Gives characters in Combine factions a voice.
///
/// <para>Four things make a noise: entering the city (a bark of gear), being hurt (a grunt), going
/// down (a death rattle), and now and then while they stand about (a radio voice, which is what
/// actually makes a city feel occupied).</para>
///
/// <para><b>Why a component and not just a system.</b> Hearing a sound at someone else's position
/// means one machine decides it happened and the others are told to play it. Hexagon does that with
/// a host-only broadcast (see its <c>BodyCues</c>), and an RPC needs a networked object to hang
/// from — so this sits on the character's own pawn, which is already networked, and
/// <see cref="CombineVoice"/> puts it there.</para>
///
/// <para><b>Who counts as Combine.</b> Hexagon's rule is that a faction grants nothing, so nothing
/// flags "this is the Combine" — only which faction asset someone belongs to. Those paths are
/// configuration, not code.</para>
/// </summary>
public sealed class CombineVoiceEmitter : Component
{
	private int _lastHealth = -1;
	private bool _lastDown;
	private bool _wasPresent;
	private RealTimeSince _sinceCue;
	private RealTimeSince _sinceIdle;
	private float _nextIdle;

	private CombineVoice Settings => CombineVoice.Current!;

	/// <summary>Whether this character belongs to one of the configured Combine factions.</summary>
	private bool IsCombine()
	{
		if ( Settings is not { } settings ) return false;

		var player = GetComponent<Hexagon.Player>( true );
		var faction = player?.FactionPath;

		return !string.IsNullOrEmpty( faction ) && settings.Factions.Contains( faction );
	}

	protected override void OnUpdate()
	{
		// Only the host decides that something happened; everyone else is told to play it.
		if ( !Networking.IsHost ) return;
		if ( !IsCombine() ) return;

		var player = GetComponent<Hexagon.Player>( true );
		if ( player is null ) return;

		// First sight of a character only records where they stand, so a reload is not a grunt.
		if ( _lastHealth < 0 )
		{
			_lastHealth = player.Health;
			_lastDown = player.IsDown;
			_wasPresent = player.HasCharacter;
			_nextIdle = Settings.IdleMinSeconds + Game.Random.NextSingle() * Settings.IdleSpreadSeconds;
			return;
		}

		// Entering the city: a bark of gear, once per arrival.
		if ( player.HasCharacter && !_wasPresent ) PlayCue( Cue.Alert );
		_wasPresent = player.HasCharacter;

		if ( player.Health < _lastHealth ) PlayCue( Cue.Pain );
		else if ( player.IsDown && !_lastDown ) PlayCue( Cue.Death );
		else if ( !player.IsDown && _lastDown ) PlayCue( Cue.Alert );
		else if ( player.HasCharacter && !player.IsDown && player.Health > 0 )
		{
			// Standing about in the city, a Combine says something now and then.
			if ( _sinceIdle > _nextIdle )
			{
				_sinceIdle = 0;
				_nextIdle = Settings.IdleMinSeconds + Game.Random.NextSingle() * Settings.IdleSpreadSeconds;
				PlayCue( Cue.Voice );
			}
		}

		_lastHealth = player.Health;
		_lastDown = player.IsDown;
	}

	private void PlayCue( Cue which )
	{
		// A burst of hits should not stack into a chorus.
		if ( Settings is not { } settings || _sinceCue < settings.MinInterval ) return;
		_sinceCue = 0;

		Play( which );
	}

	[Rpc.Broadcast( NetFlags.HostOnly | NetFlags.Unreliable )]
	private void Play( Cue which )
	{
		var sound = Settings?.SoundFor( which );
		if ( sound is not null ) GameObject.PlaySound( sound );
	}

	public enum Cue { Pain, Death, Alert, Voice }
}

/// <summary>
/// Puts a <see cref="CombineVoiceEmitter"/> on every character's pawn, and holds the sounds.
///
/// <para>The sounds are properties so they can be repointed in Project Settings without touching
/// code — the same seam Hexagon uses for its own <c>BreathingSound</c>, which its source describes
/// as "Hexagon ships none; a game gives its own". When one is left unset it falls back to the
/// imported Combine soldier set, so this makes noise out of the box.</para>
/// </summary>
public sealed class CombineVoice : GameObjectSystem<CombineVoice>
{
	/// <summary>Faction asset paths whose characters speak with Combine voices.</summary>
	[Property, Group( "Combine" )]
	public List<string> Factions { get; set; } = new()
	{
		"factions/civil_protection.faction",
		"factions/universal_union.faction",
		"factions/scanners.faction",
	};

	[Property, Group( "Combine" )] public SoundEvent? PainSound { get; set; }
	[Property, Group( "Combine" )] public SoundEvent? DeathSound { get; set; }
	[Property, Group( "Combine" )] public SoundEvent? AlertSound { get; set; }
	[Property, Group( "Combine" )] public SoundEvent? VoiceSound { get; set; }

	/// <summary>The step a Combine boot puts down. Unset, it falls back to the engine's own
	/// concrete footsteps, which suits a city.</summary>
	[Property, Group( "Combine" )] public SoundEvent? StepSound { get; set; }

	/// <summary>How far a character travels between steps, in units (one unit is an inch). A
	/// shorter stride means more frequent steps.</summary>
	[Property, Group( "Combine" )] public float WalkStride { get; set; } = 90f;
	[Property, Group( "Combine" )] public float RunStride { get; set; } = 55f;

	/// <summary>Seconds between cues from the same character.</summary>
	[Property, Group( "Combine" )] public float MinInterval { get; set; } = 1.5f;

	/// <summary>Idle speech: shortest gap between a character's remarks, and the extra random
	/// spread on top. Kept long so a city murmurs rather than babbles.</summary>
	[Property, Group( "Combine" )] public float IdleMinSeconds { get; set; } = 45f;
	[Property, Group( "Combine" )] public float IdleSpreadSeconds { get; set; } = 90f;

	public CombineVoice( Scene scene ) : base( scene )
	{
		Listen( Stage.StartUpdate, 0, Attach, nameof( CombineVoice ) );
	}

	public SoundEvent? SoundFor( CombineVoiceEmitter.Cue which ) => which switch
	{
		CombineVoiceEmitter.Cue.Pain => PainSound ?? Fallback( "combine_pain" ),
		CombineVoiceEmitter.Cue.Death => DeathSound ?? Fallback( "combine_death" ),
		CombineVoiceEmitter.Cue.Alert => AlertSound ?? Fallback( "combine_gear" ),
		_ => VoiceSound ?? Fallback( "vo/alert1" ),
	};

	/// <summary>The step a Combine boot makes. Kept separate from <see cref="SoundFor"/> because
	/// footsteps are not a cue — the imported Combine set has none.
	///
	/// <para>The fallback is our own event rather than the engine's <c>footstep-concrete</c>, which is
	/// authored at <b>half volume</b> and reads as a soft tap rather than a boot. Ours reuses the same
	/// samples at full volume and slightly lowered pitch, which is what gives a step its weight.</para>
	/// </summary>
	public SoundEvent? StepSoundFor() =>
		StepSound
		?? ResourceLibrary.Get<SoundEvent>( "sounds/combine/footstep_combine.sound" )
		?? ResourceLibrary.Get<SoundEvent>( "sounds/footsteps/footstep-concrete.sound" );

	private static SoundEvent? Fallback( string name ) =>
		ResourceLibrary.Get<SoundEvent>( $"sounds/combine/combine_soldier/{name}.sound" );

	/// <summary>Gives every character's pawn an emitter, once. A pawn is replaced when a character
	/// enters the city, so this keeps checking.</summary>
	private void Attach()
	{
		if ( !Networking.IsHost ) return;

		foreach ( var player in Scene.GetAllComponents<Hexagon.Player>() )
		{
			if ( !player.GameObject.IsValid() ) continue;
			if ( player.GameObject.GetComponent<CombineVoiceEmitter>( true ) is null )
				player.GameObject.AddComponent<CombineVoiceEmitter>();

			if ( player.GameObject.GetComponent<CombineFootsteps>( true ) is null )
				player.GameObject.AddComponent<CombineFootsteps>();
		}
	}
}
