using Sandbox;

namespace Hl2rp;

/// <summary>
/// Console commands for getting to the citizens, so nobody has to hunt the map for one in the dark.
///
/// <para>Hexagon already ships <c>hexagon_teleport</c> and a <c>goto</c> command, but those work on
/// characters in its roster, and these citizens are not characters — they have no account behind
/// them. So they need commands of their own.</para>
///
/// <para>Run from the console: <c>hl2rp_citizens</c> lists them and where each one is;
/// <c>hl2rp_goto Sena</c> puts you beside her.</para>
/// </summary>
public static class NpcTeleportCommands
{
	/// <summary>Lists every citizen, where they are, and how far off each one is.</summary>
	[ConCmd( "hl2rp_citizens" )]
	public static void List()
	{
		var scene = Sandbox.Game.ActiveScene;
		if ( scene is null ) { Log.Info( "hl2rp_citizens: no active scene." ); return; }

		var me = Hexagon.Player.Local?.WorldPosition;

		foreach ( var npc in scene.GetAllComponents<CitizenNpc>() )
		{
			var where = npc.WorldPosition;
			var far = me is null ? "" : $"  ({Vector3.DistanceBetween( me.Value, where ):0} away)";
			var off = npc.GameObject.Enabled ? "" : "  [disabled]";

			Log.Info( $"  {npc.CitizenName,-10} {where}{far}{off}" );
		}
	}

	/// <summary>
	/// Tells a citizen to walk with you, without going through chat at all.
	///
	/// <para>This exists because the chat route is not yet trustworthy: whether a line reaches a
	/// citizen depends on parsing Hexagon's transcript, and if that parse is wrong the order is
	/// dropped silently — the citizen simply never hears it. This command sets the same thing
	/// directly, so the <i>movement</i> can be tested on its own. Once following is seen working
	/// this way, any remaining failure is in the chat path and nowhere else.</para>
	/// </summary>
	[ConCmd( "hl2rp_follow" )]
	public static void Follow( string name = "" )
	{
		var scene = Sandbox.Game.ActiveScene;
		if ( scene is null ) { Log.Info( "hl2rp_follow: no active scene." ); return; }

		var me = Hexagon.Player.Local?.CharacterName ?? "you";

		foreach ( var npc in scene.GetAllComponents<CitizenNpc>() )
		{
			if ( name.Length > 0 && !npc.CitizenName.Contains( name, System.StringComparison.OrdinalIgnoreCase ) )
				continue;

			npc.Following = me;
			Log.Info( $"hl2rp_follow: {npc.CitizenName} is now following '{me}'." );
		}
	}

	/// <summary>Stops them following. <c>hl2rp_follow stop</c>.</summary>
	[ConCmd( "hl2rp_stop" )]
	public static void Stop( string name = "" )
	{
		var scene = Sandbox.Game.ActiveScene;
		if ( scene is null ) return;

		foreach ( var npc in scene.GetAllComponents<CitizenNpc>() )
		{
			if ( name.Length > 0 && !npc.CitizenName.Contains( name, System.StringComparison.OrdinalIgnoreCase ) )
				continue;

			npc.Following = null;
			Log.Info( $"hl2rp_stop: {npc.CitizenName} has stopped." );
		}
	}

	/// <summary>
	/// What you would see if you looked at a citizen — the same line the HUD will show once it is
	/// wired up. Until this session's work is finished, this is how to check recognition by hand:
	/// <c>hl2rp_look Sena</c> shows "Stranger" and what she looks like, and shows her name after she
	/// has given it.
	/// </summary>
	[ConCmd( "hl2rp_look" )]
	public static void Look( string name = "" )
	{
		var scene = Sandbox.Game.ActiveScene;
		if ( scene is null ) { Log.Info( "hl2rp_look: no active scene." ); return; }

		var me = Hexagon.Player.Local?.CharacterName;

		foreach ( var npc in scene.GetAllComponents<CitizenNpc>() )
		{
			if ( name.Length > 0 && !npc.CitizenName.Contains( name, System.StringComparison.OrdinalIgnoreCase ) )
				continue;

			Log.Info( $"  --- {npc.NameFor( me )} ---" );
			Log.Info( $"      {npc.DescribeFor( me )}" );
		}
	}

	/// <summary>
	/// Puts you beside a citizen, given any part of their name. Placed a few steps away rather than
	/// exactly on top of them, because standing inside someone is a poor way to introduce yourself.
	/// </summary>
	[ConCmd( "hl2rp_goto" )]
	public static void Goto( string name )
	{
		var scene = Sandbox.Game.ActiveScene;
		if ( scene is null ) { Log.Info( "hl2rp_goto: no active scene." ); return; }
		if ( string.IsNullOrWhiteSpace( name ) ) { Log.Info( "hl2rp_goto <name>  — try hl2rp_citizens to see who is here." ); return; }

		var npc = scene.GetAllComponents<CitizenNpc>()
			.FirstOrDefault( n => n.CitizenName.Contains( name, System.StringComparison.OrdinalIgnoreCase ) );

		if ( npc is null )
		{
			Log.Info( $"hl2rp_goto: no citizen matching '{name}'. Try hl2rp_citizens." );
			return;
		}

		var me = Hexagon.Player.Local;
		if ( me is null ) { Log.Info( "hl2rp_goto: no local player." ); return; }

		// Stand off to one side of them, and a little back, so they are in view rather than in the face.
		var beside = npc.WorldPosition + npc.WorldRotation.Forward * -90f;

		// Through Hexagon's own teleport, not by setting the transform. A pawn's position belongs to
		// the controller simulating it, so writing it directly is overwritten on the next frame —
		// which looks exactly like blinking and being put back. HostTeleport moves the host's belief
		// *and* tells the owning client's pawn, which is the only way the move sticks.
		me.HostTeleport( beside );

		Log.Info( $"hl2rp_goto: moved to {npc.CitizenName} at {npc.WorldPosition}." );
	}
}
