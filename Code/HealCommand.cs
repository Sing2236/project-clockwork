using System.Linq;
using Sandbox;

namespace Hl2rp;

/// <summary>
/// Puts a character's health back to full — the one thing Hexagon's own operator commands cannot do.
///
/// <para><b>Why it is needed.</b> Hexagon's commands cover harm, not mending. <c>hexagon_hurt</c>
/// takes health away. <c>hexagon_revive</c> stands up somebody who is <i>down</i>, and
/// <c>hexagon_restore</c> brings back somebody who is <i>dead</i>, but both refuse a character who is
/// simply standing there wounded, and both set health to <c>Vitals.Revived</c> — which is 25, not
/// 100. So somebody on their feet and hurt, or brought back and left at a quarter, had no way up.
/// This is that way.</para>
///
/// <para><b>Why it lives here and not in the framework.</b> Hexagon's operator commands are
/// Hexagon's; this is a game's addition to them, so it sits with the rest of the game's own code.
/// It leans on the framework's parts rather than numbers of its own — the roster to find the
/// character, <c>Vitals.Full</c> for what "whole" means, and <c>Characters.Changed</c> to announce
/// it — so if the framework's idea of whole ever moves, this moves with it.</para>
///
/// <para><b>The one thing it does not do.</b> It does not clear wounds. A limp or a wound is a
/// separate thing from lost health, with its own treatment, and healing somebody's body should not
/// quietly mend a broken leg. Wounds are still treated with medical items.</para>
/// </summary>
public static class HealCommand
{
	[ConCmd( "hexagon_heal" )]
	public static void Heal( string characterName )
	{
		// The console belongs to whoever owns the server, and this changes somebody's character, so
		// it runs on the host — the same footing every other operator command stands on.
		if ( !Networking.IsHost ) return;

		if ( Hexagon.HexagonServices.Current is not { Roster: { } roster, Characters: { } characters } )
		{
			Log.Info( "hexagon_heal: no city is running." );
			return;
		}

		if ( string.IsNullOrWhiteSpace( characterName ) )
		{
			Log.Info( "Usage: hexagon_heal \"<character name>\" — puts a living character back to full health." );
			return;
		}

		if ( roster.Named( characterName ) is not { } character )
		{
			Log.Info( $"hexagon_heal: nobody called '{characterName}'. Try hexagon_who." );
			return;
		}

		if ( character.IsDead )
		{
			// The dead keep their names, so the name finds them — but a death is undone, not healed.
			Log.Info( $"hexagon_heal: {character.Name} is dead. Use hexagon_restore to bring them back." );
			return;
		}

		var was = character.Health;
		character.Health = Hexagon.Logic.Vitals.Full;

		// Whoever is in the city with that character is saved through their pawn, which keeps where
		// they stand as well as the figure; anybody else is written straight to the roster.
		if ( Game.ActiveScene?.GetAllComponents<Hexagon.Player>()
			.FirstOrDefault( player => player.HostCharacter?.Id == character.Id ) is { } inTheCity )
			inTheCity.HostSave();
		else
			roster.Save( character );

		// And say so, or the player goes on seeing the old figure. The health a client shows itself —
		// in Hexagon's HUD and in the Combine suit's display — is a copy the host sent it, held apart
		// from the stored character. Writing the character alone heals the record and not the person:
		// 25 to 100 would be invisible until something else happened to refresh it. Changed is the
		// framework's own way of announcing that a character's own client should be shown it again.
		characters.Changed( character );

		Log.Info( $"hexagon_heal: {character.Name} is whole again ({was} -> {character.Health})." );
	}
}
