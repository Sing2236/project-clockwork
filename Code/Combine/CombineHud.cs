using System.Collections.Generic;
using Sandbox;

namespace Hl2rp;

/// <summary>How a line on the suit's display reads: what it is telling the wearer it has found.</summary>
public enum SuitTone
{
	/// <summary>The suit talking to itself. Nothing has happened.</summary>
	Idle,

	/// <summary>Harm, or the suit going down.</summary>
	Warning,

	/// <summary>Mending, but not whole yet.</summary>
	Repair,

	/// <summary>Whole again.</summary>
	Restored
}

/// <summary>One line the suit has read out, and how long ago.</summary>
public readonly record struct SuitLine( string Text, SuitTone Tone, RealTimeSince Age );

/// <summary>
/// The Combine suit's display: the lines it reads out to its wearer across the top of the screen.
///
/// <para><b>What this is.</b> A Combine unit's helmet talks. It reports bodily harm, reports itself
/// mending, reports shutting down, and between times mutters telemetry to itself. Clockwork's HL2RP
/// dressed that as a stack of lines at the top left, each typed out a character at a time and left
/// for eight seconds, coloured by what it was saying — and nothing about it was networked, because
/// it is the suit speaking to the person wearing it and to nobody else.</para>
///
/// <para><b>Why the local player's own state and no RPC.</b> Hexagon keeps health, faction and name
/// owner-side on purpose (<c>Player.cs</c>: "a name or a faction is knowledge, not appearance"), and a
/// client is told its own by owner-only RPC. So the wearer's own client already knows everything this
/// needs, which is exactly the shape the original had: the display is per-viewer and never travels.
/// On a dedicated server <c>Player.Local</c> is null and this does nothing at all.</para>
///
/// <para><b>Who it shows for.</b> The Combine bloc. Which factions those are is not decided here —
/// see <see cref="IsCombine"/>.</para>
///
/// <para><b>What is missing, and why it is not faked.</b> Clockwork also reported <i>armour</i>:
/// exhausted, damaged, regenerating, restored. Hexagon has no armour statistic anywhere, so there is
/// nothing for those four lines to report and they are left out rather than invented.</para>
/// </summary>
public sealed class CombineHud : GameObjectSystem<CombineHud>
{
	/// <summary>The prompt every line begins with, the suit's own cursor.</summary>
	public const string Prefix = "<:: ";

	/// <summary>How long a line takes to type out, per character. Roughly a frame, as the original was.</summary>
	public const float SecondsPerCharacter = 0.012f;

	/// <summary>How long a line stays on the display before it is taken down, in seconds.</summary>
	[Property, Group( "Combine" )] public float LineSeconds { get; set; } = 8f;

	/// <summary>
	/// The least time between two reports of harm, in seconds. Damage arrives in bursts, and a burst
	/// should read as one report rather than a dozen.
	/// </summary>
	[Property, Group( "Combine" )] public float WarningCooldown { get; set; } = 2f;

	/// <summary>How often the suit says something to itself when nothing is wrong, in seconds.</summary>
	[Property, Group( "Combine" )] public float IdleSeconds { get; set; } = 3f;

	/// <summary>The most lines kept on the display at once; the oldest fall off the top.</summary>
	[Property, Group( "Combine" )] public int MaximumLines { get; set; } = 8;

	/// <summary>Lines on this client's display, oldest first. Only ever the local wearer's own.</summary>
	public static List<SuitLine> Lines { get; } = new();

	/// <summary>Bumped whenever the display changes, so the panel knows to rebuild.</summary>
	public static int Version { get; private set; }

	/// <summary>
	/// What the suit mutters to itself. The originals were Clockwork's; the one that named Clockwork
	/// itself is replaced, since that name means nothing in a city this framework did not build.
	/// </summary>
	private static readonly string[] IdleChatter =
	{
		"Transmitting physical transition vector...",
		"Modulating external temperature levels...",
		"Parsing view ports and data arrays...",
		"Translating Union practicalities...",
		"Updating biosignal co-ordinates...",
		"Downloading recent dictionaries...",
		"Pinging connection to network...",
		"Updating mainframe connection...",
		"Synchronizing locational data...",
		"Translating radio messages...",
		"Emptying outgoing pipes...",
		"Sensoring proximity...",
		"Pinging loopback...",
		"Idle connection...",
		"Validating local data cache..."
	};

	private int _lastHealth = -1;
	private RealTimeSince _sinceWarning;
	private RealTimeSince _sinceIdle;
	private int _lastIdle = -1;
	private bool _wasCombine;

	public CombineHud( Scene scene ) : base( scene )
	{
		// Statics outlive a session in the editor, so a new city starts with a dark display rather
		// than the last thing the previous one said. The same reason Chat clears its log.
		Lines.Clear();
		Version++;

		Listen( Stage.StartUpdate, 0, Watch, nameof( CombineHud ) );
	}

	/// <summary>
	/// Whether this character belongs to a faction the suit is issued to. The list is deliberately not
	/// kept here: it is <see cref="CombineVoice.Factions"/>, the one place in this game that says who
	/// the Combine are, which <see cref="CombineFootsteps"/> already borrows. A second copy of that
	/// list would be a second answer to the same question, and the two would drift.
	/// </summary>
	private static bool IsCombine( Hexagon.Player player )
	{
		if ( CombineVoice.Current is not { } settings ) return false;

		var faction = player.FactionPath;
		return !string.IsNullOrEmpty( faction ) && settings.Factions.Contains( faction );
	}

	private void Watch()
	{
		// Expire first, gate second: a line still has to come down while the display is dark, or
		// changing faction would leave the last thing said hanging on the screen for ever.
		Prune();

		var local = Hexagon.Player.Local;
		if ( local is not { HasCharacter: true } player || !IsCombine( player ) )
		{
			// Someone else's helmet, or nobody's. The display goes dark and forgets.
			if ( _wasCombine )
			{
				Lines.Clear();
				Version++;
				_lastHealth = -1;
				_wasCombine = false;
			}
			return;
		}

		_wasCombine = true;
		var health = player.Health;

		// First sight of a wearer only records where they stand, so walking into the city is not
		// reported to the wearer as trauma. The same discipline the voice emitter uses.
		if ( _lastHealth < 0 )
		{
			_lastHealth = health;
			_sinceWarning = 999f;
			_sinceIdle = 0f;
			return;
		}

		if ( health != _lastHealth )
		{
			if ( _sinceWarning >= WarningCooldown )
			{
				if ( health < _lastHealth )
					Say( health <= 0 ? "ERROR! Shutting down..." : "WARNING! Physical bodily trauma detected...", SuitTone.Warning );
				else
					Say( health >= 100 ? "Physical body systems restored..." : "Physical body systems regenerating...",
						health >= 100 ? SuitTone.Restored : SuitTone.Repair );

				_sinceWarning = 0f;
			}

			_lastHealth = health;
		}

		// The suit talking to itself is most of what makes it feel like a Combine suit.
		if ( _sinceIdle >= IdleSeconds )
		{
			Say( NextIdle(), SuitTone.Idle );
			_sinceIdle = 0f;
		}
	}

	/// <summary>Puts a line on the display, typed from the beginning, and drops the oldest if the
	/// display is full.</summary>
	private void Say( string text, SuitTone tone )
	{
		Lines.Add( new SuitLine( Prefix + text, tone, 0f ) );
		while ( Lines.Count > MaximumLines ) Lines.RemoveAt( 0 );
		Version++;
	}

	/// <summary>Takes down the lines that have had their time.</summary>
	private void Prune()
	{
		if ( Lines.Count == 0 ) return;
		if ( Lines.RemoveAll( line => line.Age > LineSeconds ) > 0 ) Version++;
	}

	/// <summary>A line of telemetry, never the same one twice running.</summary>
	private string NextIdle()
	{
		if ( IdleChatter.Length < 2 ) return IdleChatter[ 0 ];

		int pick;
		do { pick = (int)( Game.Random.NextSingle() * IdleChatter.Length ); }
		while ( pick == _lastIdle || pick >= IdleChatter.Length );

		_lastIdle = pick;
		return IdleChatter[ pick ];
	}
}
