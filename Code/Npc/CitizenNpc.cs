using Sandbox;

namespace Hl2rp;

/// <summary>
/// A citizen with nobody behind it — an NPC that stands in the city, hears what is said near it,
/// and answers through a language model.
///
/// <para><b>Why this lives here and not in Hexagon.</b> Hexagon is built around connected players:
/// its <c>Player</c> is the pawn of a network connection and its chat delivers from a <c>Player</c>.
/// There is no seam for a character with no account, so this is the game's own, laid beside the
/// framework rather than threaded through it.</para>
///
/// <para><b>Honest limits of this first version.</b> It speaks through Hexagon's notice channel
/// rather than as a named speaker, because Hexagon's speech path requires a <c>Player</c>. Its HUD
/// presence, and speaking in its own voice on its own line, come next. It also does not yet move,
/// and it reacts to nearby players rather than to other NPCs.</para>
/// </summary>
public sealed class CitizenNpc : Component
{
	/// <summary>Tag on an NPC's body, so traces that must ignore it can say so.</summary>
	public const string NpcTag = "npc";

	/// <summary>
	/// The controller's ground braking, set to half the engine's default of 1.
	///
	/// <para><b>Why, measured rather than guessed.</b> <c>BrakePower</c> is documented as "extra
	/// friction when we're on the ground and our desired velocity is lower than our current
	/// velocity". A player's controller knows its desired velocity, because it reads the keyboard,
	/// so that only bites when the keys are let go. A citizen's controller has
	/// <c>UseInputControls = false</c> and therefore has no input at all: its desired velocity reads
	/// as zero on every single frame, so the brake is applied against the very movement it is being
	/// asked to make. At the default of 1 the brake wins and the body crawls — driven at a wish of
	/// 50 it travelled at 8, and the harder it was pushed the worse the fraction, which is why the
	/// citizens looked stalled however their pace was set. Measured over one second in one open
	/// direction, at a wish of 50:</para>
	///
	/// <list type="bullet">
	/// <item><c>1.0</c> — 8.0 u/s, stops in 0.10s (the engine's default: cannot move).</item>
	/// <item><c>0.5</c> — 50.3 u/s, stops in 0.30s over 5 units (the whole wish, and a firm stop).</item>
	/// <item><c>0.25</c> — 50.5 u/s, stops in 0.51s.</item>
	/// <item><c>0.0</c> — 49.7 u/s, and it never stops: it coasted 300 units and was still going.</item>
	/// </list>
	///
	/// <para>Half is the point where the wish is answered in full and the body can still be brought
	/// to rest sharply. Nothing else in the controller needed changing: walk and run speeds, the
	/// acceleration times, friction values, mass and damping were each varied and made no
	/// difference to it.</para>
	/// </summary>
	public const float NpcBrakePower = 0.5f;

	[Property] public string CitizenName { get; set; } = "Malcolm";
	[Property] public string CitizenDescription { get; set; } = "a tired-looking man in a worn coat";

	/// <summary>
	/// How this one is his own person — his temperament, what he wants, what he is afraid of. Two
	/// citizens with the same description but a different personality should not read alike.
	/// </summary>
	[Property, TextArea] public string Personality { get; set; } =
		"Tired and cautious. Speaks little, and only when he must. Assumes the worst of anyone in uniform.";

	/// <summary>How far he can tell who is standing around him, in units.</summary>
	[Property] public float AwareRange { get; set; } = 600f;

	/// <summary>
	/// What the model last decided to do with its feet, if anything. This is the equivalent of a
	/// key held down: <see cref="CitizenWander"/> reads it and walks him that way, and the model is
	/// what put it there.
	/// </summary>
	public MoveUrge? Urge { get; set; }

	/// <summary>Who last spoke to him, so he can turn towards or away from them.</summary>
	public Vector3? SpeakingFrom { get; set; }

	/// <summary>
	/// A stranger is what they look like, and only becomes a name once they have given it. This is
	/// who has been told who he is — kept per viewer, because knowing someone is a thing one person
	/// has and another does not.
	/// </summary>
	private readonly HashSet<string> _knownBy = new( System.StringComparer.OrdinalIgnoreCase );

	/// <summary>True once this citizen has given their name to that viewer.</summary>
	public bool IsKnownTo( string? viewer ) =>
		!string.IsNullOrEmpty( viewer ) && _knownBy.Contains( viewer );

	/// <summary>What somebody sees when they look at him: a name if he has given it, and otherwise
	/// only what he looks like.</summary>
	public string NameFor( string? viewer ) => IsKnownTo( viewer ) ? CitizenName : "Stranger";

	/// <summary>The line shown under the name.</summary>
	public string DescribeFor( string? viewer ) =>
		IsKnownTo( viewer ) ? $"A citizen of City 17." : CitizenDescription;

	/// <summary>Tells that viewer who he is. Called when he introduces himself — which is a thing he
	/// chooses to do, not something that happens merely because someone asked.</summary>
	public void IntroduceTo( string? viewer )
	{
		if ( !string.IsNullOrEmpty( viewer ) ) _knownBy.Add( viewer! );
	}

	public enum MoveUrge { Toward, Away, Stop, Follow }

	/// <summary>Who he has taken up with, while following. Held by name, because that is what
	/// survives a pawn being rebuilt when someone re-enters the city.</summary>
	public string? Following { get; set; }

	/// <summary>How far away speech still reaches this citizen, in units.</summary>
	[Property] public float HearRange { get; set; } = 700f;

	/// <summary>Seconds it will wait between replies, so it cannot be made to babble.</summary>
	[Property] public float SpeakCooldown { get; set; } = 8f;

	/// <summary>
	/// How long he keeps attending to whoever last spoke to him, in seconds, after the last thing
	/// said. This is what holds him still through an exchange: wandering is what he does with
	/// himself when nobody wants him, and while somebody does, it can wait. Long enough to cover the
	/// pauses between lines of a conversation, short enough that he goes back to his own business
	/// when it dries up.
	/// </summary>
	[Property] public float EngageSeconds { get; set; } = 15f;

	/// <summary>Who last addressed him, so he can keep facing them while they talk rather than at
	/// where they were standing when they started.</summary>
	public string? AddressedBy { get; set; }

	/// <summary>
	/// Whether somebody is talking to him. True from the moment a line reaches him until
	/// <see cref="EngageSeconds"/> after the last one.
	/// </summary>
	public bool Engaged => _sinceAddressed < EngageSeconds;

	private TimeSince _sinceAddressed = 999f;

	/// <summary>The Ollama model to think with.</summary>
	[Property] public string Model { get; set; } = "llama3.2:latest";

	/// <summary>
	/// Where the model is asked. This must obey <c>Sandbox.Http</c>'s rules for loopback: a
	/// <b>hostname</b> rather than a bare IP, and a port among 80, 443, 8080 or 8443. Ollama's own
	/// default of 11434 is refused outright, and so is <c>127.0.0.1</c> whatever port it is on — so
	/// Ollama has to be told to listen on 8080 and addressed as "localhost".
	/// </summary>
	[Property] public string OllamaUrl { get; set; } = "http://localhost:8080/api/generate";

	private SkinnedModelRenderer? _renderer;
	private RealTimeSince _sinceSpoke;
	private bool _thinking;

	/// <summary>The child the model is drawn on, so it can be hidden when he is not on his feet.</summary>
	private GameObject? _bodyObject;

	/// <summary>
	/// <b>Host: the body and the character are two things, and this is the character.</b> Hexagon's
	/// own words: a character in the city is in its pawn, one whose player left while it was down is
	/// a sleeper, and one that is neither is <i>only its document</i>. The document is the person;
	/// bodies come and go. This is the citizen's document — his health, his wounds, whether he is
	/// down — and it is what <see cref="CitizenBody"/> carries, so that he can be shot, wounded and
	/// downed like anyone else, and be found again after a restart.
	/// </summary>
	public Hexagon.Logic.CharacterData? Character =>
		Hexagon.HexagonServices.Current?.Roster?.Find( CharacterId );

	/// <summary>The character's id, so that his health and history follow the person rather than the
	/// scene object he is built from.</summary>
	public System.Guid CharacterId { get; private set; }

	/// <summary>
	/// Whether he is on his feet and his own to walk about. A citizen with no character at all —
	/// because the city had none to give him — still counts as standing, so that failing to become a
	/// character leaves him exactly as he was rather than frozen.
	/// </summary>
	public bool Standing =>
		Character is not { } character || ( !character.IsDead && !character.IsDown );

	/// <summary>Which faction he belongs to, as the asset path of a <c>.faction</c> file. A citizen
	/// belongs among the citizens unless somebody says otherwise.</summary>
	[Property] public string FactionPath { get; set; } = "factions/citizens.faction";

	/// <summary>Overrides the body he is built with. Left empty, the faction chooses it — see
	/// <see cref="BodyModel"/>.</summary>
	[Property] public string ModelPath { get; set; } = "";

	/// <summary>
	/// The body this citizen is built with.
	///
	/// <para>A civilian wears the schema's own <c>citizen_human</c>; a Civil Protection unit wears the
	/// Combine Soldier from the workshop's <c>playermodelports.combine</c> package. Neither is
	/// converted from Source 1 - both are real s&box models - so the faction alone decides the
	/// look.</para>
	///
	/// <para>Two things cost a lot of time here, so they are worth writing down. There is <b>no
	/// metrocop model anywhere in s&box</b>; the Combine Soldier is the closest that exists, and it
	/// stands in for Civil Protection. And a package asset is <b>not available the moment it
	/// installs</b> - <c>combinesoldier/combine.vmdl</c> returns <c>No asset</c> for a minute or so
	/// afterwards, then compiles and appears. Judge it with <c>asset_info</c>, never with
	/// <c>Model.Load</c>: that returns non-null even for a model that cannot resolve, and s&box
	/// draws those as the literal word "error" instead of failing, which is exactly how a CP unit
	/// ended up standing in the city as "error".</para>
	/// </summary>
	private string BodyModel
	{
		get
		{
			if ( !string.IsNullOrWhiteSpace( ModelPath ) )
				return ModelPath;

			// This is only the fallback. What a CP unit actually ends up as is decided by his
			// wardrobe, not here: DressFor dresses him, and Looks.Apply takes the body from that
			// look. The HumanSkin piece is what puts him on the schema's human, which is the only
			// way he gets the same head as everybody else - the engine citizen's is larger, and
			// dressing the armour onto it is what produced a head and a mask that both read as
			// oversized. Body and wardrobe have to agree, and wearing the body is how they do.
			return FactionPath.ToLowerInvariant().Contains( "civil_protection" )
				? "models/citizen/citizen.vmdl"
				: "models/citizen_human/citizen_human_male.vmdl";
		}
	}

	/// <summary>
	/// The metrocop body armour, from the workshop's <c>playermodelports.metrocopclothes</c>.
	///
	/// <para>He wears the schema's human under it - see <see cref="HumanSkin"/> - which is what puts
	/// him on the same body, and so the same head, as everybody else. The armour itself covers only
	/// <c>Chest, Legs, Hands, Feet</c> and carries no helmet, so the head shows above it; the gas mask
	/// is what covers that.</para>
	/// </summary>
	private const string MetrocopSuit = "playermodels/metrocop_clothes/metrocop_clothes.clothing";

	/// <summary>
	/// The skin he wears under the armour, and the thing that decides his body.
	///
	/// <para>A "human" piece is the library's way of saying which body a character has: it carries
	/// <c>HasHumanSkin</c> and names the schema's human as its <c>HumanSkinModel</c>. Wearing it is
	/// what gives a CP unit the same head as everyone else rather than the engine citizen's.</para>
	/// </summary>
	private const string HumanSkin = "models/citizen_clothes/body/human01.clothing";

	/// <summary>
	/// The mask over the face, worn on the human body the skin piece gives him.
	///
	/// <para>Its size is not a property of the mask: every hat in the library ships two models, one
	/// for the engine's citizen and one <c>_m_human</c> for the schema's human, and the dresser
	/// picks between them from the body. Worn on the human, the human pair is the one that fits -
	/// on the engine citizen it came out visibly oversized, which is what the earlier attempts ran
	/// into.</para>
	/// </summary>
	private const string GasMask = "models/citizen_clothes/hat/gasmask/gasmask.clothing";

	/// <summary>
	/// What the civilians wear. Each is a whole look - a human skin first, then clothes - because
	/// that is what the dressing path takes, and because the skin is what decides the body.
	///
	/// <para>The blue jumpsuit the citizens wear in Half-Life 2 - the same overalls over the same
	/// shirts, which is what makes a crowd of them read as City 17 rather than as a crowd of
	/// individuals. The only thing that varies is the skin underneath, so the city does not look
	/// like one person repeated.</para>
	///
	/// <para>Which one a citizen wears is settled by his name, so he keeps it across restarts
	/// rather than changing clothes every time the city loads.</para>
	/// </summary>
	private static readonly string[] CitizenOutfits =
	{
		"models/citizen_clothes/body/human01.clothing;" +
		"models/citizen_clothes/shirt/jumpsuit/blue_jumpsuit.clothing;" +
		"models/citizen_clothes/shoes/boots/black_boots.clothing",

		"models/citizen_clothes/body/human03.clothing;" +
		"models/citizen_clothes/shirt/jumpsuit/blue_jumpsuit.clothing;" +
		"models/citizen_clothes/shoes/boots/army_boots.clothing",

		"models/citizen_clothes/body/human05.clothing;" +
		"models/citizen_clothes/shirt/jumpsuit/blue_jumpsuit.clothing;" +
		"models/citizen_clothes/shoes/boots/black_boots.clothing",

		"models/citizen_clothes/body/human02.clothing;" +
		"models/citizen_clothes/shirt/jumpsuit/blue_jumpsuit.clothing;" +
		"models/citizen_clothes/shoes/boots/army_boots.clothing",

		"models/citizen_clothes/body/human06.clothing;" +
		"models/citizen_clothes/shirt/jumpsuit/blue_jumpsuit.clothing;" +
		"models/citizen_clothes/shoes/boots/black_boots.clothing",

		"models/citizen_clothes/body/human04.clothing;" +
		"models/citizen_clothes/shirt/jumpsuit/blue_jumpsuit.clothing;" +
		"models/citizen_clothes/shoes/boots/army_boots.clothing",

		"models/citizen_clothes/body/human07.clothing;" +
		"models/citizen_clothes/shirt/jumpsuit/blue_jumpsuit.clothing;" +
		"models/citizen_clothes/shoes/boots/black_boots.clothing",
	};

	/// <summary>
	/// Which outfit a civilian wears, settled by his name.
	///
	/// <para>Not <c>string.GetHashCode</c>: that is randomised per process, so a citizen would wear
	/// something different every launch. A plain sum of the characters is stable, and stable is the
	/// whole point - he should look the same when the city reloads.</para>
	/// </summary>
	private string OutfitFor( string name )
	{
		var sum = 0;
		foreach ( var c in name ) sum = ( sum + c ) % 4096;
		return CitizenOutfits[sum % CitizenOutfits.Length];
	}

	/// <summary>
	/// Puts him in his faction's uniform, where it has one.
	///
	/// <para>A body has to be one the engine will animate, and a uniform is clothing worn over it - so
	/// Civil Protection is the engine's own citizen wearing the metrocop suit, not an imported
	/// Combine model. This goes through <see cref="Hexagon.Looks.Apply"/>, the framework's own
	/// dressing path, rather than a ClothingContainer built here: it is how every character in the
	/// city is dressed, and it knows what cannot be worn together.</para>
	/// </summary>
	private void DressFor()
	{
		if ( !_renderer.IsValid() ) return;

		// The framework's own dressing path, and the only one that has produced armour. It chooses
		// the body itself, from the look rather than from BodyModel - and the way to choose it is to
		// wear it, which is what the human-skin piece does: it names the schema's human as its
		// HumanSkinModel, and ModelOf reads exactly that. So CP wears the human as his skin, and
		// gets its head - the same head everybody else in the city has - rather than the engine
		// citizen's larger one, which is what BodyModel's own citizen used to give him.
		if ( FactionPath.ToLowerInvariant().Contains( "civil_protection" ) )
		{
			Hexagon.Looks.Apply( _renderer, $"{BodyModel};{HumanSkin};{MetrocopSuit};{GasMask}" );
			return;
		}

		// Everyone else wears ordinary city clothes - see CitizenOutfits. His outfit starts with a
		// human skin for the same reason CP's does: it is what decides which body he has.
		Hexagon.Looks.Apply( _renderer, OutfitFor( CitizenName ) );
	}

	/// <summary>
	/// What a Civil Protection unit carries. A sidearm and a stun baton: the two things a unit is
	/// never without, and the two he is trusted with before anything else.
	///
	/// <para>Issued through the framework's own transfer path rather than written into his document
	/// by hand, so it lands in the journal like anything else that enters the city. Issued only when
	/// he does not already have one, because this runs every time the city loads and a unit should
	/// not accumulate a pistol per restart.</para>
	/// </summary>
	private void IssueLoadout( Hexagon.Logic.CharacterData character )
	{
		if ( !FactionPath.ToLowerInvariant().Contains( "civil_protection" ) ) return;
		if ( Hexagon.HexagonServices.Current?.Transfers is not { } transfers ) return;

		foreach ( var path in new[] { "items/pistol.item", "items/stunbaton.item" } )
		{
			if ( character.Inventory.Items.Any( item => item.Definition == path ) ) continue;

			// Actor and Sources sit with the framework's rule layer rather than with its components,
			// which is why they are `Hexagon.Logic` and ItemDefinition is not.
			Hexagon.ItemDefinition.Find( path )
				?.IssueTo( transfers, Hexagon.Logic.Sources.Arrival, character, Hexagon.Logic.Actor.Of( character ) );
		}
	}

	/// <summary>Hides or shows the model. When he is down, the body on the ground is the one to be
	/// seen; when he is dead there is nothing of him left to show.</summary>
	public void HideBody( bool hidden )
	{
		if ( _bodyObject.IsValid() ) _bodyObject.Enabled = !hidden;
	}

	protected override void OnStart()
	{
		if ( !Networking.IsHost ) return;

		BuildBody();
		_sinceSpoke = 999f;
	}

	/// <summary>The citizen's body. Hexagon puts the animated model on a child so the controller's
	/// duck-bob does not drag the root; this does the same, for the same reason.</summary>
	private void BuildBody()
	{
		// A controller of his own, the same one a player's pawn gets. Hexagon adds this for
		// connections; an NPC has to be given it. Movement, gravity, ground and steps all come
		// with it, which is what makes him behave like anyone else rather than a prop that slides.
		var controller = GameObject.AddComponent<PlayerController>();

		// A controller reads the keyboard by default, which is right for a player and quite wrong
		// for a citizen: without this, every NPC in the city is steered by whoever is typing, and
		// they all walk about as that person does. Nothing drives him but his own wishes.
		controller.UseInputControls = false;
		controller.UseCameraControls = false;
		controller.UseLookControls = false;

		// And the brake, which a controller with no input to read applies to itself without ever
		// being asked to. See <see cref="NpcBrakePower"/> — at the engine's default of 1 this body
		// moves at a sixth of the pace it is given.
		controller.BrakePower = NpcBrakePower;

		// A player's pawn is network-spawned by Hexagon; this one was only a scene object, and so
		// had no owner at all. A PlayerController without an owning connection has nobody to
		// simulate for, which is why it went on asking to move and the body never did.
		GameObject.NetworkMode = NetworkMode.Object;
		GameObject.NetworkSpawn( Connection.Host );

		// Gravity, which a controller's body does not come with by default. Its absence is what made
		// the citizens drift rather than stand, and is the first thing to check if they float again.
		if ( controller.Body.IsValid() )
			controller.Body.Gravity = true;

		var body = new GameObject( GameObject, true, "Body" );
		_renderer = body.AddComponent<SkinnedModelRenderer>();
		_renderer.Model = Sandbox.Model.Load( BodyModel );
		_renderer.CreateBoneObjects = true;

		// What puts him in a pose at all. A model's animation graph does nothing on its own: with
		// nothing feeding it parameters the skeleton holds its bind pose, and a humanoid bind pose
		// is a T-pose. The helper reads the controller on the parent and sets move_x, move_y,
		// b_grounded and the rest every frame, which is what makes him walk rather than glide.
		var animation = body.AddComponent<Sandbox.Citizen.CitizenAnimationHelper>();
		animation.Target = _renderer;

		// Hand the graph over directly when the model did not bring one of its own.
		//
		// The schema's citizen names its graph in the .vmdl and arrives with it already attached, so
		// this is a no-op for him. A ported model does not: combine.vmdl names the very same graph
		// and s&box silently drops it - the compiled file contains the reference, yet
		// Model.AnimGraph comes back null, so the skeleton never moves. Assigning it here is what
		// actually binds it, and it is the standard way to retarget a port onto a skeleton it shares
		// bones with but was not authored against.
		if ( _renderer.Model?.AnimGraph is null )
		{
			var graph = ResourceLibrary.Get<AnimationGraph>( "models/citizen_human/animgraphs/human.vanmgrph" );
			if ( graph is not null )
			{
				_renderer.AnimationGraph = graph;
				_renderer.UseAnimGraph = true;
			}
		}

		// And what he wears, through the framework's own clothing system rather than by hand:
		// Looks.Apply builds a ClothingContainer and dresses the renderer with a Dresser, which is
		// how Hexagon dresses every character. Wearing the suit is what makes a citizen read as
		// Civil Protection - the body is the engine's, and the uniform is the clothing asset.
		DressFor();

		// Linked explicitly, as Hexagon does: the automatic link runs before the child exists.
		controller.Renderer = _renderer;
		_bodyObject = body;

		// The character behind the body: what makes him a person rather than a prop.
		BecomeACharacter();

		// No collider is added here. A PlayerController builds its own with the object, and a second
		// static box standing in the same place is simply a wall the controller cannot walk out of —
		// which is what pinned the citizens in one spot no matter how hard they wished to move.
		// Hexagon's own pawn does the same: the renderer is decoration, the controller owns collision.
		body.Tags.Add( NpcTag );
	}

	/// <summary>
	/// Host: gives him a character, or finds the one he already is.
	///
	/// <para>Until this ran there was nothing behind the body at all — nothing to harm, nothing to
	/// down, nothing to search, nothing that could remember being shot. See
	/// <see cref="AdoptOrCreate"/> for how a character comes to exist for somebody with no account,
	/// and <see cref="CitizenBody"/> for what carries it.</para>
	/// </summary>
	private void BecomeACharacter()
	{
		if ( Hexagon.HexagonServices.Current is not { Roster: { } roster } ) return;

		if ( AdoptOrCreate( roster ) is not { } character ) return;

		CharacterId = character.Id;

		// Where he stands now is his, and whose he is. Written straight through, so that a citizen
		// new to the city has somewhere to come back to.
		character.Position = new[] { WorldPosition.x, WorldPosition.y, WorldPosition.z };
		character.Faction = FactionPath ?? string.Empty;
		roster.Save( character );

		// The body the framework knows how to harm. It looks for its embodiment on its own
		// GameObject — no ancestor search — so the two go on the root together.
		var holder = GameObject.AddComponent<CitizenBody>();
		holder.CharacterId = character.Id;
		GameObject.AddComponent<Hexagon.CharacterBody>();
		GameObject.AddComponent<Hexagon.BodyCues>();

		// The hitboxes a shot is traced against. A player's pawn is given these by the framework in
		// its OnStart; a citizen has to be given them here. Hexagon's own helper for this,
		// `Bodies.AddHitboxes`, is internal to the framework and cannot be called from here, so this
		// does the same thing: a model hitbox group on a child of the model, reporting the root, so
		// that a strike on it looks up and finds the CharacterBody sitting one step above.
		//
		// Without them a bullet passes clean through. The trace strikes nothing carrying an
		// IDamageable, which is why every attack ever fired in this city is recorded as
		// "hit: nothing" — the pistol was working the whole time and had nothing it could reach.
		if ( _renderer.IsValid() )
		{
			var hitboxes = new GameObject( _renderer.GameObject, true, "Hitboxes" ) { NetworkMode = NetworkMode.Never };
			var shapes = hitboxes.AddComponent<ModelHitboxes>();
			shapes.Renderer = _renderer;
			shapes.Target = GameObject;
		}

		IssueLoadout( character );

		// Who he has become, if this is not his first evening in the city.
		NpcNotes.Apply( character.Id, this );
	}

	/// <summary>
	/// Host: the character he already is, or a new one if he has never been here.
	///
	/// <para><b>Adopt before creating, because a character must be the same person each evening.</b>
	/// Hexagon mints one through <c>CharacterRoster.Create</c>, the only way a character is ever made:
	/// it invents an id, enforces the account's limit on how many may be alive, and refuses a name
	/// that is already someone's — the dead keep their names, so a name is never reused. A citizen has
	/// no account to make one for, and must be the <i>same</i> character every time the city loads, or
	/// he would arrive a stranger each evening with a full health bar and no memory of being shot. So
	/// the name is looked up first: the same name is the same person.</para>
	///
	/// <para>Only if no such citizen has ever existed is one made, under the accountless id and with
	/// the limit lifted — every citizen in the city shares that accountless id, and would otherwise
	/// count against one another until only the first of them could exist at all.</para>
	/// </summary>
	private Hexagon.Logic.CharacterData? AdoptOrCreate( Hexagon.Logic.CharacterRoster roster )
	{
		if ( roster.Named( CitizenName ) is { } existing ) return existing;

		var made = roster.Create(
			steamId: 0,
			name: CitizenName,
			description: CitizenDescription,
			now: System.DateTimeOffset.UtcNow,
			rules: new Hexagon.Logic.LifeRules( 100000, System.TimeSpan.Zero ),
			body: null );

		if ( made.Ok ) return made.Value;

		// Not fatal. He stays exactly as he was — a citizen with nothing behind him — and can still
		// be talked to and still walk about; he simply cannot be hurt.
		Log.Warning( $"[citizen] {CitizenName} could not be made a character: {made.Message}" );
		return null;
	}

	/// <summary>
	/// Host only: something was said nearby. Decided here rather than by a client, so a modified
	/// client cannot make the citizen say whatever it likes.
	/// </summary>
	public void Heard( string speaker, string text, Vector3 where )
	{
		if ( !Networking.IsHost ) return;
		if ( Vector3.DistanceBetween( where, WorldPosition ) > HearRange ) return;

		// Somebody has spoken to him: whatever he does with the words, and whether or not he can
		// answer them yet, he stops walking and attends. Set before the cooldown and the thinking
		// checks on purpose — a citizen too far into his own reply to say anything is still being
		// spoken to, and should still turn and stand.
		AddressedBy = speaker;
		_sinceAddressed = 0f;

		if ( _thinking ) return;
		if ( _sinceSpoke < SpeakCooldown ) return;

		_ = Respond( speaker, text );
	}

	public void Heard( string speaker, string text, Vector3 where, Vector3 from )
	{
		SpeakingFrom = from;
		Heard( speaker, text, where );
	}

	private async System.Threading.Tasks.Task Respond( string speaker, string text )
	{
		_thinking = true;
		_sinceSpoke = 0f;

		var reply = await Ask( speaker, text );

		_thinking = false;

		if ( string.IsNullOrWhiteSpace( reply ) ) return;

		// Saying your own name is introducing yourself, so that is what tells him apart from a
		// stranger afterwards.
		if ( reply.Contains( CitizenName, System.StringComparison.OrdinalIgnoreCase ) )
			IntroduceTo( speaker );

		// A movement directive, if the model chose to walk. Taken off the front so the rest of the
		// line reads as ordinary speech.
		if ( reply.StartsWith( "[follow stop]" ) ) { Urge = MoveUrge.Stop; reply = reply[13..].Trim(); }
		else if ( reply.StartsWith( "[follow]" ) ) { Urge = MoveUrge.Follow; reply = reply[8..].Trim(); }
		else if ( reply.StartsWith( "[move toward]" ) ) { Urge = MoveUrge.Toward; reply = reply[13..].Trim(); }
		else if ( reply.StartsWith( "[move away]" ) ) { Urge = MoveUrge.Away; reply = reply[11..].Trim(); }
		else if ( reply.StartsWith( "[move stop]" ) ) { Urge = MoveUrge.Stop; reply = reply[11..].Trim(); }

		// Remember it, so the next exchange has a past. Oldest falls away first.
		_memory.Add( new Memory( speaker, text, reply.Trim() ) );
		if ( _memory.Count > MemoryDepth ) _memory.RemoveAt( 0 );

		// Now and then he takes stock of himself. Who a person is at the end of a week in this city
		// is not who they were at the start of it, and a citizen whose temper never moves is a prop
		// rather than a person.
		_sinceReflected += 1;
		if ( _sinceReflected >= ReflectEvery ) { _sinceReflected = 0; _ = Reflect(); }

		// A line beginning with / is something the citizen does; anything else is something it says.
		// Written in Hexagon's own house style (Logic/ChatRules.Format) rather than a style of our
		// own: "{name} says \"{text}\"" and "** {name} {text}". A line that does not match those
		// shapes cannot be read back by <see cref="CitizenHearing"/>, and a citizen speaking in a
		// format only he understands is a citizen nobody can answer.
		if ( reply.StartsWith( "/me " ) )
		{
			Speak( $"** {CitizenName} {reply[4..].Trim()}" );
		}
		else if ( reply.StartsWith( "/me" ) )
		{
			Speak( $"** {CitizenName} {reply[3..].Trim()}" );
		}
		else
		{
			Speak( $"{CitizenName} says \"{reply.Trim()}\"" );
		}
	}

	/// <summary>Every this many exchanges, he reconsiders who he has become.</summary>
	[Property] public int ReflectEvery { get; set; } = 5;

	private int _sinceReflected;

	/// <summary>
	/// Asks him, in his own voice, how the city has changed him — and lets the answer replace what
	/// he was. Kept deliberately small: one sentence, and the old personality is offered to him as
	/// a starting point rather than discarded, so he drifts rather than lurching.
	/// </summary>
	private async System.Threading.Tasks.Task Reflect()
	{
		var whatHasPassed = _memory.Count == 0
			? "nothing of note"
			: string.Join( " ", _memory.Select( m => $"{m.Who} said \"{m.Said}\" and you answered \"{m.YouSaid}\"." ) );

		var prompt =
			$"""
			You are {CitizenName}, {CitizenDescription}, a citizen under Combine occupation in City 17.

			Until now you have been: {Personality}

			Recent things that have happened to you: {whatHasPassed}

			You are not the same person you were. Write one sentence describing who you are now —
			your temper, what you want, what you fear. Keep it in the same form as the description
			above: plain, in the third person, as someone else would size you up. No preamble, no
			quotation marks, one sentence only.
			""";

		var revised = await Complete( prompt );

		if ( !string.IsNullOrWhiteSpace( revised ) && revised.Length < 400 )
		{
			Personality = revised.Trim();

			// Written down as well as held, or who he became lasts only until the city closes: the
			// scene holds what he was authored as, and Hexagon has nowhere to keep a personality.
			NpcNotes.Remember( CharacterId, Personality );
		}
	}

	/// <summary>
	/// What he can see of who is standing near him, as he would put it to himself. His sense of a
	/// uniform is the faction the person belongs to, which is the only thing Hexagon keeps.
	/// </summary>
	private string WhoIsAround()
	{
		var seen = new List<string>();

		foreach ( var other in Scene.GetAllComponents<Hexagon.Player>() )
		{
			if ( !other.IsValid() || !other.HasCharacter ) continue;
			if ( Vector3.DistanceBetween( other.WorldPosition, WorldPosition ) > AwareRange ) continue;

			var faction = other.FactionPath ?? string.Empty;

			var asHeSees = faction.Contains( "civil_protection" ) ? "a Civil Protection officer"
				: faction.Contains( "universal_union" ) ? "one of the Union's soldiers"
				: faction.Contains( "scanners" ) ? "a scanner"
				: faction.Contains( "vortigaunts" ) ? "a vortigaunt"
				: "another citizen";

			if ( !seen.Contains( asHeSees ) ) seen.Add( asHeSees );
		}

		// What he remembers of the last few things said to him, oldest first.
		var recollection = _memory.Count == 0
			? "Nobody has spoken to you yet."
			: string.Join( " ", _memory.Select( m => $"{m.Who} said \"{m.Said}\" and you answered \"{m.YouSaid}\"." ) );

		var around = seen.Count == 0
			? "Nobody else is nearby."
			: $"Nearby you can see {string.Join( ", ", seen )}.";

		// What he is doing with himself, so he answers as someone in the middle of something rather
		// than as a voice with no body. Only what he could know of himself: his own feet, his own
		// intentions, and how far he has strayed from where he started.
		var moving = GetComponent<CitizenWander>( true );
		var controller = GetComponent<PlayerController>( true );

		var doing = Following is { } companion
			? $"You are walking along with {companion}, keeping near them."
			: controller is { WishVelocity.Length: > 1f }
				? "You are on your feet and walking somewhere."
				: "You are standing still, going nowhere in particular.";

		var strayed = moving is null ? 0f : Vector3.DistanceBetween( WorldPosition, moving.HomePosition );

		var where = strayed < 150f
			? "You are on the patch of the city you know, near where you usually are."
			: strayed < 600f
				? "You have drifted a little way from where you usually are."
				: "You are further from home than you like to be, and not sure of the way back.";

		return $"{around}\n{doing}\n{where}\nWhat you remember of late: {recollection}\n\n";
	}

	/// <summary>One thing said to him, and what he said back — what he carries into the next
	/// exchange, so a conversation can have a shape rather than starting cold every time.</summary>
	private readonly record struct Memory( string Who, string Said, string YouSaid );

	/// <summary>The last few exchanges only. A citizen is not a ledger, and a prompt that grows
	/// without end is a prompt that stops being about the moment.</summary>
	private readonly List<Memory> _memory = new();
	private const int MemoryDepth = 4;

	/// <summary>Asks the model, in character, and hands back a single line of reply.</summary>
	private async System.Threading.Tasks.Task<string> Ask( string speaker, string text )
	{
		var prompt =
			$"""
			You are {CitizenName}, {CitizenDescription}, a citizen living under Combine occupation
			in City 17.

			Who you are: {Personality}

			Rules you must follow:
			- Never break character or mention that you are an AI, a model, or a game.
			- Never describe yourself in the third person; you are speaking aloud.
			- Keep replies under 25 words. Say one thing.
			- If you would physically do something, reply with a line beginning "/me ", for example
			  "/me glances over his shoulder".
			- Otherwise reply with speech alone, with no quotation marks.

			You may also decide to move, exactly as if you had pressed a key to walk. If so, put one
			of these at the very start of your reply, before anything else:
			  [move toward]  - walk towards whoever is speaking to you
			  [move away]    - back away from them
			  [move stop]    - stand still
			  [follow]       - go along with them, and keep going with them until told otherwise
			  [follow stop]  - stop going along with them
			Only move when it is what {CitizenName} would actually do. Most replies should not move.
			If they ask you to come with them or to follow, agree or refuse as {CitizenName} would —
			and if you agree, use [follow].

			{WhoIsAround()}{speaker} has just said to you: "{text}"

			Reply with only what {CitizenName} says or does.
			""";

		try
		{
			var payload = new System.Text.Json.Nodes.JsonObject
			{
				[ "model" ] = Model,
				[ "prompt" ] = prompt,
				[ "stream" ] = false,
			};

			// Sandbox.Http, not HttpClient: game code runs under the engine's assembly whitelist and
			// System.Net.Http is refused outright. This is the sanctioned way out to the network.
			//
			// The JsonObject goes in as it is. Handing CreateJsonContent a string instead serialises
			// it a second time, and Ollama answers 400 — "cannot unmarshal string into Go value".
			var body = await Http.RequestStringAsync( OllamaUrl, "POST", Http.CreateJsonContent( payload ) );

			var node = System.Text.Json.Nodes.JsonNode.Parse( body );

			return node?[ "response" ]?.GetValue<string>()?.Trim() ?? string.Empty;
		}
		catch ( System.Exception e )
		{
			Log.Warning( $"[citizen] {CitizenName} could not think: {e.Message}" );
			return string.Empty;
		}
	}

	/// <summary>Puts a bare prompt to the model and hands back its answer, with no character of its
	/// own — used both for speech and for the occasional reckoning with himself.</summary>
	private async System.Threading.Tasks.Task<string> Complete( string prompt )
	{
		try
		{
			var payload = new System.Text.Json.Nodes.JsonObject
			{
				[ "model" ] = Model,
				[ "prompt" ] = prompt,
				[ "stream" ] = false,
			};

			var body = await Http.RequestStringAsync( OllamaUrl, "POST", Http.CreateJsonContent( payload ) );
			var node = System.Text.Json.Nodes.JsonNode.Parse( body );

			return node?[ "response" ]?.GetValue<string>()?.Trim() ?? string.Empty;
		}
		catch ( System.Exception e )
		{
			Log.Warning( $"[citizen] {CitizenName} could not think: {e.Message}" );
			return string.Empty;
		}
	}

	/// <summary>Host: puts a line in the city's chat. Hexagon's notice channel, for now — its speech
	/// path needs a Player, which an NPC has not got.</summary>
	[Rpc.Broadcast( NetFlags.HostOnly | NetFlags.Reliable )]
	private void Speak( string line ) => Hexagon.Chat.Note( line );
}

/// <summary>
/// Lets citizens hear what is said near them.
///
/// <para>Hexagon delivers speech from a <c>Player</c> to a <c>Player</c>, so there is no callback an
/// NPC can subscribe to. What it does expose is <c>Chat.Lines</c>, the city's running transcript, so
/// this reads the tail of it and hands new lines to any citizen close enough to have heard them.
/// Polling is less elegant than a hook, but it needs nothing of Hexagon.</para>
///
/// <para>The speaker is taken from the line's own text, since the transcript keeps only what was
/// said. That is enough for a citizen's purposes: it needs to know who is talking, not to trust them.</para>
/// </summary>
public sealed class CitizenHearing : GameObjectSystem<CitizenHearing>
{
	private int _seen;

	public CitizenHearing( Scene scene ) : base( scene )
	{
		Listen( Stage.StartUpdate, 0, Hear, nameof( CitizenHearing ) );
	}

	private void Hear()
	{
		if ( !Networking.IsHost ) return;

		var lines = Hexagon.Chat.Lines;

		// First look only establishes where the transcript has got to, so reloading the scene does
		// not set every citizen off about a conversation that is already over.
		if ( _seen == 0 ) { _seen = lines.Count; return; }

		if ( lines.Count < _seen ) _seen = lines.Count;   // cleared, or trimmed to MaximumLines
		if ( lines.Count == _seen ) return;

		var fresh = lines.Skip( _seen ).ToArray();
		_seen = lines.Count;

		var citizens = Scene.GetAllComponents<CitizenNpc>().ToArray();
		if ( citizens.Length == 0 ) return;

		foreach ( var line in fresh )
		{
			if ( line.IsNotice ) continue;               // the city talking to itself, not to anyone
			if ( string.IsNullOrWhiteSpace( line.Text ) ) continue;

			var (speaker, said) = Split( line.Text );
			if ( said.Length == 0 ) continue;

			// Speech is not a broadcast. A remark in a street is aimed at someone — by name, or by
			// being said to whoever is nearest. Without this the whole city answers at once, which
			// is the opposite of a conversation.
			var addressed = citizens.Where( c => Names( said, c.CitizenName ) ).ToArray();

			if ( addressed.Length > 0 )
			{
				foreach ( var citizen in addressed )
				{
					// What was asked for is a fact and is acted on; how it is answered is the model's.
					if ( AsksToFollow( said ) ) { citizen.Following = speaker; Log.Info( $"[citizen] {citizen.CitizenName} will follow '{speaker}'." ); }
					else if ( AsksToStopFollowing( said ) ) { citizen.Following = null; Log.Info( $"[citizen] {citizen.CitizenName} has been told to stay." ); }

					citizen.Heard( speaker, said, citizen.WorldPosition );
				}

				continue;
			}

			// Nobody named: the closest one answers. Where the speaker is has to be worked out,
			// because Hexagon's transcript keeps what was said and not where from.
			var speakerAt = SpeakerPosition( speaker ) ?? NearAnyPlayer();

			if ( speakerAt is null )
			{
				// Nowhere to measure from. Rather than always picking the same citizen — which is how
				// one man ends up answering for the whole city — let whoever is nearest to hand reply.
				var anyInReach = citizens.Where( c => c.WorldPosition.Distance( citizens[ 0 ].WorldPosition ) < 2000f ).ToArray();
				if ( anyInReach.Length > 0 )
					Game.Random.FromArray( anyInReach )!.Heard( speaker, said, citizens[ 0 ].WorldPosition );

				continue;
			}

			var nearest = citizens
				.OrderBy( c => Vector3.DistanceBetween( c.WorldPosition, speakerAt.Value ) )
				.FirstOrDefault( c => Vector3.DistanceBetween( c.WorldPosition, speakerAt.Value ) <= c.HearRange );

			if ( nearest is not null )
			{
				if ( AsksToFollow( said ) ) { nearest.Following = speaker; Log.Info( $"[citizen] {nearest.CitizenName} will follow '{speaker}'." ); }
				else if ( AsksToStopFollowing( said ) ) { nearest.Following = null; Log.Info( $"[citizen] {nearest.CitizenName} has been told to stay." ); }

				nearest.Heard( speaker, said, nearest.WorldPosition );
			}
		}
	}

	/// <summary>Whether a line names a citizen. Said to "Malcolm" or to "Malcolm," — either way his.</summary>
	private static bool Names( string said, string name ) =>
		said.Contains( name, System.StringComparison.OrdinalIgnoreCase );

	/// <summary>
	/// Whether someone has asked to be followed, or asked for it to stop.
	///
	/// <para>This is read from the <i>speaker's</i> words and not from the model's reply, on purpose.
	/// A small model will happily answer "I'll follow you" and emit no instruction at all, so the
	/// citizen agrees and then stands still — which is worse than refusing. What someone asks for
	/// is a fact about the world; what the model does about it is characterisation. Acting on the
	/// former, and letting the latter colour how it is said, is the only way this works reliably.</para>
	/// </summary>
	private static bool AsksToFollow( string said ) =>
		said.Contains( "follow me", System.StringComparison.OrdinalIgnoreCase )
		|| said.Contains( "come with me", System.StringComparison.OrdinalIgnoreCase )
		|| said.Contains( "come along", System.StringComparison.OrdinalIgnoreCase )
		|| said.Contains( "with me", System.StringComparison.OrdinalIgnoreCase );

	private static bool AsksToStopFollowing( string said ) =>
		said.Contains( "stay here", System.StringComparison.OrdinalIgnoreCase )
		|| said.Contains( "wait here", System.StringComparison.OrdinalIgnoreCase )
		|| said.Contains( "stop following", System.StringComparison.OrdinalIgnoreCase )
		|| said.Contains( "stay put", System.StringComparison.OrdinalIgnoreCase );

	/// <summary>Where the speaker is, found by their character's name, so "nearest" means nearest to
	/// whoever actually spoke rather than to the first citizen in the list.</summary>
	private static Vector3? SpeakerPosition( string speaker )
	{
		foreach ( var player in Sandbox.Game.ActiveScene.GetAllComponents<Hexagon.Player>() )
		{
			if ( player.CharacterName?.Equals( speaker, System.StringComparison.OrdinalIgnoreCase ) == true )
				return player.WorldPosition;
		}

		return null;
	}

	/// <summary>Where anyone at all is. Usually the person who spoke is the one in front of the
	/// screen, and in a listen server that is the local player.</summary>
	private static Vector3? NearAnyPlayer()
	{
		if ( Hexagon.Player.Local is { } local )
			return local.WorldPosition;

		foreach ( var player in Sandbox.Game.ActiveScene.GetAllComponents<Hexagon.Player>() )
		{
			if ( player.HasCharacter ) return player.WorldPosition;
		}

		return null;
	}

	/// <summary>
	/// The ways Hexagon writes a spoken line, from <c>Logic/ChatRules.Format</c>. These are the
	/// exact shapes and they are the only shapes: a player's line is
	/// <c>{name} says "{text}"</c>, and there is <b>no colon</b> before the quote.
	/// </summary>
	private static readonly string[] SpokenVerbs =
	{
		" says \"", " whispers \"", " yells \"", " radios \""
	};

	/// <summary>
	/// Takes a transcript line apart into who said it and what they said, or falls back to
	/// treating the whole line as speech by an unnamed speaker.
	///
	/// <para><b>This is the whole of "the citizens will not follow me", and it was one character
	/// wide.</b> The ear used to look for <c>" says: "</c> — with a colon — but Hexagon's chat
	/// writes <c>" says "</c> without one. Every player's line therefore failed to parse, the
	/// speaker came out as "Someone", and a follow order was recorded against a name that no
	/// player has. <see cref="CitizenWander"/> resolves the name it is given each frame, could not
	/// find "Someone", and took that to mean the person had left the city — so it cleared the order
	/// on the very next frame. The citizen agreed to come along and then stood still, which is
	/// exactly what was seen. Matching Hexagon's real formats is the fix; the colon is gone.</para>
	/// </summary>
	private static (string Speaker, string Said) Split( string text )
	{
		// The spoken forms. The speaker is everything before the verb; the words are between the
		// quotes that follow it. Nothing a speaker's name can contain ends in these, so the first
		// match is the separator and not part of somebody's name.
		foreach ( var verb in SpokenVerbs )
		{
			var at = text.IndexOf( verb, System.StringComparison.Ordinal );
			if ( at > 0 )
				return (text[..at].Trim(), text[(at + verb.Length)..].Trim().TrimEnd( '"' ).Trim());
		}

		// An action, not speech: "** Name does something". The name is the first word.
		if ( text.StartsWith( "** ", System.StringComparison.Ordinal ) )
		{
			var rest = text[3..].Trim();
			var space = rest.IndexOf( ' ' );
			return space > 0 ? (rest[..space], rest[(space + 1)..].Trim()) : (rest, string.Empty);
		}

		// Out of character: "[OOC] Name: something".
		if ( text.StartsWith( "[OOC] ", System.StringComparison.Ordinal ) )
		{
			var rest = text[6..].Trim();
			var colon = rest.IndexOf( ':' );
			return colon > 0 ? (rest[..colon].Trim(), rest[(colon + 1)..].Trim()) : (rest, string.Empty);
		}

		return ("Someone", text.Trim().Trim( '"' ));
	}
}
