using Sandbox;

namespace Hl2rp;

/// <summary>
/// A citizen who is on his feet, as the framework sees him: the body his character is carried in.
///
/// <para><b>Why this has to exist.</b> Hexagon knows three ways for a character to have a body. In the
/// city it is in its pawn — a <see cref="Hexagon.Player"/>, which is by definition a <i>connection's</i>
/// pawn: its chat, its authorization, its camera and its <c>Local</c> all key on <c>Network.Owner</c>,
/// so one cannot be made for a citizen who has no connection. Down or bound with no pawn, a character
/// is carried by a <see cref="Hexagon.Sleeper"/>. And a character that is neither is <b>only its
/// document</b> — which is what our citizens were: a real <c>CharacterData</c> with nobody to put it
/// in. This is the missing fourth case: a character standing in the world with no player behind it.</para>
///
/// <para><b>Modelled on Sleeper, not on Player.</b> <see cref="Hexagon.Sleeper"/> is the framework's
/// own precedent for a body with no connection — host-owned, host-simulated, resolving its character
/// by id rather than by owner — so this follows it: <see cref="Hexagon.IEmbodiment"/> only, no connection
/// assumptions, and the character found through the roster by id.</para>
///
/// <para><b>The two members that matter.</b> <see cref="Hexagon.IEmbodiment.HostCharacter"/> and
/// <see cref="Hexagon.IEmbodiment.IsShownHere"/> must both go false once he is <i>down</i>. Not for tidiness:
/// <c>CharacterBody.HoldTarget</c> is <c>GetComponent&lt;Sleeper&gt;() ?? GetComponent&lt;Player&gt;()</c>,
/// which is null on a body that is neither, and the drag/carry/lead verbs — offered whenever
/// <c>IsDown</c> is true — dereference it. Letting the downed body keep claiming to be shown would put
/// a null straight through those verbs. Going false the moment he is down is exactly how a downed
/// <i>player's</i> pawn steps aside for its sleeper, and it means the limp body left on the ground is
/// the one that offers to be searched and carried.</para>
/// </summary>
public sealed class CitizenBody : Component, Hexagon.IEmbodiment
{
	/// <summary>The character this body carries. Set by <see cref="CitizenNpc"/> as it builds him.</summary>
	public System.Guid CharacterId { get; set; }

	/// <summary>The character, found the way a sleeper finds its own: by id, through the roster.</summary>
	private Hexagon.Logic.CharacterData? Character =>
		Hexagon.HexagonServices.Current?.Roster?.Find( CharacterId );

	private CitizenNpc? Npc => GetComponent<CitizenNpc>( true );

	private SkinnedModelRenderer? Draws => GetComponent<PlayerController>( true )?.Renderer;

	public bool IsDown => Character?.IsDown ?? false;

	System.Guid Hexagon.IEmbodiment.CharacterId => CharacterId;
	bool Hexagon.IEmbodiment.IsDown => IsDown;
	bool Hexagon.IEmbodiment.IsRestrained => Character?.IsRestrained ?? false;

	string Hexagon.IEmbodiment.CharacterDescription => Npc?.CitizenDescription ?? string.Empty;

	/// <summary>Shown, and so able to be looked at and acted on — until he is down, when the body on
	/// the ground is the one that should be offering itself for searching and carrying.</summary>
	bool Hexagon.IEmbodiment.IsShownHere => !IsDown;

	Vector3 Hexagon.IEmbodiment.HostPosition => WorldPosition;

	/// <summary>The character he carries, or null while he is down: a body on its feet is not the
	/// embodiment of a man lying on the ground.</summary>
	Hexagon.Logic.CharacterData? Hexagon.IEmbodiment.HostCharacter => Networking.IsHost && !IsDown ? Character : null;

	SkinnedModelRenderer? Hexagon.IEmbodiment.Renderer => Draws;

	bool Hexagon.IEmbodiment.IsLimp => false;
	bool Hexagon.IEmbodiment.IsLimping => Character is { } character && Hexagon.Logic.Wounds.Limps( character );
	bool Hexagon.IEmbodiment.IsBreathingHard => Character is { } character && Hexagon.Logic.Vitals.BreathesHard( character );

	string Hexagon.IEmbodiment.Look => Character is { } character ? Hexagon.CharacterService.LookOf( character ) : string.Empty;

	/// <summary>
	/// Down, he is carried by a sleeper and this body must stop standing where it fell; dead, there is
	/// nothing left for it to be. Either way the drawing and the collision go, and come back if staff
	/// put him on his feet again — which is why this is done every frame rather than destroyed.
	/// </summary>
	protected override void OnUpdate()
	{
		if ( !Networking.IsHost ) return;

		var hidden = IsDown || ( Character?.IsDead ?? false );

		if ( GetComponent<PlayerController>( true ) is { } controller && controller.ColliderObject.IsValid() )
			controller.ColliderObject.Enabled = !hidden;

		if ( Npc is { } npc ) npc.HideBody( hidden );
	}
}
