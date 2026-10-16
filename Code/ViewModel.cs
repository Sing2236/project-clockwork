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
		_sway = Vector2.Zero;
		_bob = 0f;
	}
}
