using System;
using System.Collections.Generic;
using Sandbox;

namespace Hl2rp;

/// <summary>
/// Shows what is held, from the eye, and moves the way a held thing moves.
///
/// <para>A held item is rendered on the hand - it is parented to the hand bone, on the body. In third
/// person that is all it takes. In first person the body is hidden
/// (<c>PlayerController.HideBodyInFirstPerson</c>), and the gun goes with it, because it is a child of
/// a bone that is no longer drawn. So a ViewModel is needed: the same weapon, a separate model, drawn
/// against the camera. Facepunch ship both halves of every gun - <c>w_</c> is what is carried,
/// <c>v_</c> is what is seen - and they are not interchangeable.</para>
///
/// <para>The model carries its own rig and anim graph (v_usp has 77 bones, and its arms and hands are
/// part of that rig rather than a separate asset), so it is a <see cref="SkinnedModelRenderer"/> with
/// the graph left on. What the graph does not know is how the player is moving, and there is no
/// engine helper to tell it - so the sway and the bob are done here, by moving the model rather than
/// by posing it. That is the old way of doing it and it needs no knowledge of parameter names that
/// cannot be read.</para>
/// </summary>
public sealed class ViewModel : GameObjectSystem<ViewModel>
{
	/// <summary>
	/// The first-person model for each weapon we have one for, by its world model.
	/// </summary>
	private static readonly Dictionary<string, string> FromTheEye = new()
	{
		["models/weapons/sbox_pistol_usp/w_usp.vmdl"] = "models/weapons/sbox_pistol_usp/v_usp.vmdl",
		["models/weapons/sbox_smg_mp5/w_mp5.vmdl"] = "models/weapons/sbox_smg_mp5/v_mp5.vmdl",
		["models/weapons/sbox_assault_m4a1/w_m4a1.vmdl"] = "models/weapons/sbox_assault_m4a1/v_m4a1.vmdl",
	};

	/// <summary>
	/// The animation a weapon fires with, by its world model. These are the model's own sequences -
	/// v_usp carries 118 of them, including one-handed and two-handed fire, reloads, jumps and lands -
	/// and which one is right is a property of the weapon rather than of the shot: a pistol fires
	/// one-handed, a rifle two.
	/// </summary>
	private static readonly Dictionary<string, string> FireAnimation = new()
	{
		["models/weapons/sbox_pistol_usp/w_usp.vmdl"] = "1H_Fire_01",
		["models/weapons/sbox_smg_mp5/w_mp5.vmdl"] = "2H_Fire_01",
		["models/weapons/sbox_assault_m4a1/w_m4a1.vmdl"] = "2H_Fire_01",
	};

	/// <summary>Where the model rests, in the camera's own frame: forward, right, up.</summary>
	private static readonly Vector3 Rest = new( 22f, 9f, -13f );

	/// <summary>How far the model slides back against a turn, and how quickly it catches up.</summary>
	private static readonly Vector2 Sway = new( 0.06f, 0.05f );
	private const float SwayCatchUp = 6f;

	/// <summary>How far it bobs, how fast, and how much faster running makes it.</summary>
	private const float BobAmount = 1.6f;
	private const float BobRate = 9f;
	private const float RunBonus = 0.7f;

	private GameObject _shown;
	private string _model;
	private string _held;

	/// <summary>The sway still owed, in the camera's right and up.</summary>
	private Vector2 _sway;
	private float _bob;
	private Angles _lastAngles;
	private Vector3 _lastPosition;

	public ViewModel( Scene scene ) : base( scene )
	{
		Listen( Stage.StartUpdate, 1, Update, nameof( ViewModel ) );
	}

	private void Update()
	{
		var pawn = Hexagon.Player.Local;
		var camera = Scene.Camera;

		// Nothing to show unless this client has a pawn, a camera, and a body it is looking out of.
		if ( pawn is null || camera is null )
		{
			Clear();
			return;
		}

		if ( pawn.GetComponent<PlayerController>( true ) is not { } controller || controller.ThirdPerson )
		{
			// Third person: the body is drawn and the gun on its hand with it. A first-person model
			// here would be a second gun standing inside the first.
			Clear();
			return;
		}

		var world = HeldWorldModel( pawn );
		if ( world is null || !FromTheEye.TryGetValue( world, out var eyeModel ) )
		{
			Clear();
			return;
		}

		_held = world;
		if ( _model != eyeModel ) Clear();
		if ( _shown is null )
		{
			// On the camera, so it follows the eye without being told to each frame.
			_shown = new GameObject( Scene.Camera.GameObject, true, "ViewModel" );

			// SkinnedModelRenderer, not ModelRenderer: these models carry a rig and an anim graph of
			// their own, and a plain ModelRenderer has no animator to run it - which left the gun a
			// static prop in mid-air.
			var created = _shown.AddComponent<SkinnedModelRenderer>();
			created.Model = Model.Load( eyeModel );
			created.UseAnimGraph = true;
			_model = eyeModel;
			_lastAngles = camera.WorldRotation.Angles();
			_sway = Vector2.Zero;
			_bob = 0f;
			_lastPosition = pawn.WorldPosition;
		}

		// The shot. RequestAttack is an Rpc.Host, so the host runs the damage - but the press happens
		// here first, and reading it here is what makes the animation immediate rather than a round trip.
		if ( Input.Pressed( "Attack1" ) && _shown is not null )
		{
			var playing = _shown.Components.Get<SkinnedModelRenderer>( true );
			if ( playing is not null && FireAnimation.TryGetValue( _held ?? "", out var fire )
				&& playing.Sequence.SequenceNames.Contains( fire ) )
			{
				playing.Sequence.Name = fire;
				playing.Sequence.Looping = false;
			}

			Flash( playing );
			Mark( camera, pawn );
		}

		var rotation = camera.WorldRotation;
		var angles = rotation.Angles();

		// Sway: the model lags a turn, then catches up. Read from how much the view moved this frame
		// rather than from the mouse, so it behaves the same however the view was turned.
		var turned = new Vector2(
			MathX.DeltaDegrees( angles.yaw, _lastAngles.yaw ),
			MathX.DeltaDegrees( angles.pitch, _lastAngles.pitch ) );
		_lastAngles = angles;

		_sway += new Vector2( turned.x * Sway.x, -turned.y * Sway.y );
		_sway = _sway.LerpTo( Vector2.Zero, Time.Delta * SwayCatchUp );

		// Bob: while the feet are moving. The speed is measured from where the pawn actually is, not
		// from its reported velocity - PlayerController.Velocity is documented as "our actual physical
		// velocity minus our ground velocity", so on solid ground the two cancel and it reads zero
		// even at a full run. Anything distance-based that trusts it silently never fires.
		var here = pawn.WorldPosition;
		var moved = ( here - _lastPosition ).Length;
		_lastPosition = here;

		var speed = Time.Delta > 0f ? moved / Time.Delta : 0f;
		if ( !controller.IsOnGround ) speed = 0f;
		if ( speed > 20f ) _bob += Time.Delta * BobRate * (speed > 200f ? 1f + RunBonus : 1f);

		var bob = _bob == 0f ? Vector2.Zero : new Vector2( MathF.Sin( _bob ) * BobAmount, MathF.Abs( MathF.Cos( _bob ) ) * BobAmount * 0.5f );

		_shown.WorldRotation = rotation;
		_shown.WorldPosition = camera.WorldPosition
			+ rotation.Forward * Rest.x
			+ rotation.Right * ( Rest.y + _sway.x + bob.x )
			+ rotation.Up * ( Rest.z + _sway.y - bob.y );
	}

	/// <summary>
	/// A brief light at the muzzle, for the length of one shot.
	///
	/// <para>The weapon models carry a real <c>muzzle</c> attachment - v_usp has <c>muzzle</c> and
	/// <c>eject</c> - so the flash goes where the barrel actually points rather than at a guessed
	/// offset. It is a light rather than a sprite or a particle because it needs no art to be
	/// authored first, and it is what makes a shot read at night.</para>
	/// </summary>
	private void Flash( SkinnedModelRenderer renderer )
	{
		if ( renderer.Model?.GetAttachment( "muzzle" ) is not { } muzzle ) return;

		// The attachment is in the model's own space, so it is carried out through the model's
		// transform - which already holds the sway and the bob.
		var at = _shown.WorldTransform.ToWorld( muzzle );

		var flash = new GameObject( true, "Muzzle Flash" );
		flash.WorldPosition = at.Position;
		var light = flash.AddComponent<PointLight>();
		light.LightColor = new Color( 1f, 0.85f, 0.55f );
		light.Radius = 400f;
		light.Shadows = false;

		// One shot long, then gone. Nothing keeps a reference to it.
		flash.AddComponent<ExpiresEffect>().Seconds = 0.05f;
	}

	/// <summary>Where a shot leaves a mark: a hole in the wall, or blood on a person.</summary>
	private static readonly string[] ImpactMarks =
	{
		"decals/impact/ceramic/decal_impact_ceramic_01.decal",
		"decals/impact/concrete/decal_impact_concrete_01.decal",
		"decals/impact/metal/decal_impact_metal_01.decal",
	};

	private static readonly string[] BloodMarks =
	{
		"decals/blood/blood_splatter_01.decal",
		"decals/blood/blood_splatter_02.decal",
		"decals/blood/blood_splatter_03.decal",
		"decals/blood/blood_splatter_04.decal",
	};

	/// <summary>How long a mark stays. Long enough to read the room, short enough not to pile up.</summary>
	private const float MarkSeconds = 45f;

	/// <summary>
	/// Where the shot landed, marked on whatever it hit.
	///
	/// <para>The host already traced this - RequestAttack does its own trace and applies the damage -
	/// but it never says where the trace went, so nothing on this side can know. So the shot is traced
	/// again here, the same way: from the eye, along the aim, out to the weapon's own range, against
	/// hitboxes and past the capsule that is only for walking into. The host's trace remains the
	/// authority on harm; this one is only for what it looks like.</para>
	/// </summary>
	private void Mark( CameraComponent camera, Hexagon.Player pawn )
	{
		var definition = HeldDefinition( pawn );
		var range = definition?.Range ?? 1500f;

		var from = camera.WorldPosition;
		var direction = camera.WorldRotation.Forward;
		var trace = Scene.Trace.Ray( from, from + direction * range )
			.UseHitboxes()
			.WithoutTags( "capsule" )
			.IgnoreGameObjectHierarchy( pawn.GameObject )
			.Run();

		if ( !trace.Hit ) return;

		// A person is marked in blood; anything else takes a hole.
		var person = trace.GameObject?.GetComponentInParent<Hexagon.CharacterBody>() is not null;
		var pool = person ? BloodMarks : ImpactMarks;
		var chosen = pool[ Game.Random.Int( 0, pool.Length - 1 ) ];
		var decal = ResourceLibrary.Get<DecalDefinition>( chosen );
		if ( decal is null ) return;

		var mark = new GameObject( true, person ? "Blood" : "Bullet Hole" );
		mark.WorldPosition = trace.HitPosition;
		// Faces back along the surface it landed on, with a little scatter so two shots in one place
		// do not sit exactly on top of each other.
		mark.WorldRotation = Rotation.LookAt( -trace.Normal ) * Rotation.FromYaw( Game.Random.Float( -180f, 180f ) ) * Rotation.FromRoll( Game.Random.Float( 0f, 360f ) );

		var decalComponent = mark.AddComponent<Decal>();
		decalComponent.Decals = new List<DecalDefinition> { decal };
		decalComponent.Size = person ? new Vector2( 14f, 14f ) : new Vector2( 6f, 6f );
		decalComponent.Depth = 10f;

		mark.AddComponent<ExpiresEffect>().Seconds = MarkSeconds;
	}

	/// <summary>The definition of whatever is held, found by the model it draws with.</summary>
	private static Hexagon.ItemDefinition HeldDefinition( Hexagon.Player pawn )
	{
		var world = HeldWorldModel( pawn );
		if ( world is null ) return null;

		foreach ( var d in ResourceLibrary.GetAll<Hexagon.ItemDefinition>() )
		{
			if ( d?.WorldModel is null ) continue;
			if ( d.WorldModel.ResourcePath == world ) return d;
		}

		return null;
	}

	/// <summary>The world model of whatever this pawn is holding, or null.</summary>
	private static string HeldWorldModel( Hexagon.Player pawn )
	{
		foreach ( var child in pawn.GameObject.GetAllObjects( true ) )
		{
			if ( child.Name != "Held" ) continue;
			var model = child.Components.Get<ModelRenderer>( true )?.Model;
			if ( model is not null ) return model.ResourcePath;
		}

		return null;
	}

	private void Clear()
	{
		_shown?.Destroy();
		_shown = null;
		_model = null;
		_held = null;
		_sway = Vector2.Zero;
		_bob = 0f;
	}
}
