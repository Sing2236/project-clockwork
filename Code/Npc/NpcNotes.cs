using System.Text.Json;
using Sandbox;

namespace Hl2rp;

/// <summary>What this game remembers about a citizen that Hexagon has nowhere to keep.</summary>
public sealed class NpcNote
{
	/// <summary>
	/// Who he has become. A citizen who talks long enough takes stock of himself and the model
	/// rewrites this; without somewhere to put it the rewriting is lost the moment the city closes.
	/// </summary>
	public string Personality { get; set; } = string.Empty;
}

/// <summary>
/// The game's own notes about its NPC characters, kept beside Hexagon's records rather than in them.
///
/// <para><b>Why this exists at all.</b> Making the citizens real characters buys them everything
/// <c>CharacterData</c> can hold — health, wounds, whether they are down, who they know. It does not
/// buy them a personality, because a character has no field for one and the framework is not ours to
/// add to. So what is the <i>game's</i> state about a citizen lives in the game's own file, next to
/// the city's data rather than inside it, which is also the only honest place for it: a personality
/// is this game's invention, not a fact about the world Hexagon models.</para>
///
/// <para>One small JSON document per character, keyed by the character's id so that it follows the
/// person and not the scene object — the same document survives a citizen being rebuilt from the
/// scene, which is the whole point.</para>
/// </summary>
public static class NpcNotes
{
	/// <summary>Where they live: beside the game's data, not the framework's.</summary>
	private const string Folder = "hl2rp/npc";

	private static string PathOf( System.Guid id ) => $"{Folder}/{id:N}.json";

	/// <summary>The note kept for a character, or null if there is none yet.</summary>
	public static NpcNote? Load( System.Guid id )
	{
		if ( id == System.Guid.Empty ) return null;

		try
		{
			var path = PathOf( id );
			if ( !FileSystem.Data.FileExists( path ) ) return null;

			var text = FileSystem.Data.ReadAllText( path );
			return string.IsNullOrWhiteSpace( text ) ? null : JsonSerializer.Deserialize<NpcNote>( text );
		}
		catch ( System.Exception e )
		{
			Log.Warning( $"[citizen] could not read the note for {id:N}: {e.Message}" );
			return null;
		}
	}

	/// <summary>Writes the note for a character. Failure is logged and not fatal: a lost note costs a
	/// personality, and must never cost the city.</summary>
	public static void Save( System.Guid id, NpcNote note )
	{
		if ( id == System.Guid.Empty ) return;

		try
		{
			FileSystem.Data.CreateDirectory( Folder );
			FileSystem.Data.WriteAllText( PathOf( id ), JsonSerializer.Serialize( note ) );
		}
		catch ( System.Exception e )
		{
			Log.Warning( $"[citizen] could not write the note for {id:N}: {e.Message}" );
		}
	}

	/// <summary>Puts what was remembered onto a citizen as he is built, if anything was.</summary>
	public static void Apply( System.Guid id, CitizenNpc npc )
	{
		if ( Load( id ) is not { } note ) return;
		if ( string.IsNullOrWhiteSpace( note.Personality ) ) return;

		npc.Personality = note.Personality;
	}

	/// <summary>Remembers who he has become.</summary>
	public static void Remember( System.Guid id, string personality )
	{
		if ( string.IsNullOrWhiteSpace( personality ) ) return;
		Save( id, new NpcNote { Personality = personality } );
	}
}
