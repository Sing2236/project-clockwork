using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Hl2rp.Editor;

/// <summary>
/// Menu entries laying down the factions and ranks HL2RP is played with, matching Clockwork's
/// roster: Citizens, Civil Protection (its recruit-to-elite ladder), the Universal Union's
/// Overwatch soldiers, Scanners, Vortigaunts and the Administrator.
///
/// <para><b>Why a menu and not an in-game panel.</b> In Hexagon a faction is a <c>.faction</c> asset
/// (<c>Hexagon.FactionDefinition</c>) and assets are authored, not conjured by a running game. So
/// making one belongs here, beside the file it produces. Handing one out is done in game by staff
/// through Hexagon's staff console: <c>faction "Character Name" civil_protection Unit</c>.</para>
///
/// <para><b>Where Clockwork's "abilities" actually lived.</b> Read from Cloud Sixteen's own schema,
/// a Clockwork class carried only a colour, a wage, a wage label, a faction, a default flag, a
/// description and a default physical description. It had no powers. What felt like class
/// abilities — rationing, combine locks, warrants, CID records — were the schema's systems,
/// gated on <i>faction</i>, not on class.</para>
///
/// <para><b>What Hexagon does differently.</b> A faction there "grants nothing and changes nothing
/// about its members"; kit comes from what someone carries, not from what they are. Its v0.1 notes
/// record that wages and what factions granted were deliberately removed. So a rank here says only
/// what someone may do to those below them: enlist, promote, discharge. <b>Wages are not in
/// Hexagon</b> — see Docs/PORTING.md for what matching Clockwork fully would still need.</para>
/// </summary>
public static class Hl2rpFactions
{
	[Menu( "HL2RP", "Factions/Create Citizens" )]
	public static void CreateCitizens() => Write( "citizens", "Citizens",
		"The city's population, and everyone else not in uniform.", "0.55,0.5,0.45,1",
		Rank( "Citizen" ) );

	/// <summary>Civil Protection, following Clockwork's recruit-to-elite ladder.</summary>
	[Menu( "HL2RP", "Factions/Create Civil Protection" )]
	public static void CreateCivilProtection() => Write( "civil_protection", "Civil Protection",
		"The city's police force.", "0.3,0.5,0.8,1",
		Rank( "Recruit" ),
		Rank( "Unit" ),
		Rank( "Elite", enlists: true, promotes: true, promotesTo: "Recruit" ),
		Rank( "Divisional Leader", enlists: true, promotes: true, promotesTo: "Elite", discharges: true ) );

	/// <summary>The Universal Union's transhuman soldiers.</summary>
	[Menu( "HL2RP", "Factions/Create Universal Union" )]
	public static void CreateUniversalUnion() => Write( "universal_union", "Universal Union",
		"The Union's transhuman soldiers, produced by the Combine.", "0.45,0.15,0.15,1",
		Rank( "Soldier" ),
		Rank( "Elite" ),
		Rank( "Commander", enlists: true, promotes: true, promotesTo: "Soldier", discharges: true ) );

	[Menu( "HL2RP", "Factions/Create Scanners" )]
	public static void CreateScanners() => Write( "scanners", "Scanners",
		"Metropolice scanners, utilising Combine technology.", "0.35,0.55,0.65,1",
		Rank( "Scanner" ) );

	[Menu( "HL2RP", "Factions/Create Vortigaunts" )]
	public static void CreateVortigaunts() => Write( "vortigaunts", "Vortigaunts",
		"An alien race from the planet Xen.", "0.4,0.7,0.4,1",
		Rank( "Vortigaunt" ) );

	/// <summary>The Administrator. One rank — an administrator answers to the Union, not to staff.</summary>
	[Menu( "HL2RP", "Factions/Create Administrator" )]
	public static void CreateAdministrator() => Write( "administrator", "Administrator",
		"A human Administrator advised by the Universal Union.", "1,0.78,0.39,1",
		Rank( "Administrator" ) );

	[Menu( "HL2RP", "Factions/Open Factions Folder" )]
	public static void OpenFolderFolder()
	{
		var folder = Path.Combine( Project.Current.GetAssetsPath(), "factions" );
		Directory.CreateDirectory( folder );
		EditorUtility.OpenFileFolder( folder );
	}

	private static object Rank( string title, bool enlists = false, bool promotes = false,
		string promotesTo = "", bool discharges = false, string insignia = "" ) => new
	{
		Title = title,
		Insignia = insignia,
		Enlists = enlists,
		Promotes = promotes,
		PromotesTo = promotesTo,
		Discharges = discharges,
	};

	/// <summary>
	/// Writes a faction asset, refusing to clobber one already there — the folder may hold a faction
	/// someone has since tailored, and losing that to a menu entry would be unkind.
	/// </summary>
	private static void Write( string file, string title, string description, string tint,
		params object[] ranks )
	{
		var folder = Path.Combine( Project.Current.GetAssetsPath(), "factions" );
		Directory.CreateDirectory( folder );

		var path = Path.Combine( folder, $"{file}.faction" );

		if ( File.Exists( path ) )
		{
			Log.Info( $"[HL2RP] '{file}.faction' already exists - left alone." );
			return;
		}

		// The shape the [AssetType] serialiser writes, so the editor reads it back cleanly.
		var node = JsonSerializer.SerializeToNode( new { Title = title, Description = description, Tint = tint, Ranks = ranks } )!.AsObject();
		node["__references"] = new JsonArray();
		node["__version"] = 0;

		File.WriteAllText( path, node.ToJsonString( new JsonSerializerOptions { WriteIndented = true } ) );

		// The editor watches the assets folder, so it compiles the new asset on its own.
		Log.Info( $"[HL2RP] Wrote 'factions/{file}.faction' with {ranks.Length} rank(s)." );
	}
}
