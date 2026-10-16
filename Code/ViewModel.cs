using System.Collections.Generic;
using Sandbox;

namespace Hl2rp;

/// <summary>
/// Shows what is held, from the eye.
///
/// <para>A held item is rendered on the hand - it is parented to the hand bone, on the body. In third
/// person that is all it takes: the body is visible, so the gun on it is too. In first person the
/// body is hidden (<c>PlayerController.HideBodyInFirstPerson</c>), and the gun goes with it, because
/// it is a child of a bone that is no longer drawn. So nothing you pick up can be seen from the eye,
/// which is exactly the hole a ViewModel fills: the same weapon, a separate model, drawn against the
/// camera rather than on a hand.</para>
///
/// <para>This is setting code, not framework code, like <see cref="FirstPerson"/> beside it.</para>
/// </summary>
public sealed class ViewModel : GameObjectSystem<ViewModel>
{
	/// <summary>
	/// The first-person model for each weapon we have one for, by its world model. Facepunch ship
	/// both halves of every gun they make - the <c>w_</c> name is what is carried, the <c>v_</c> name
	/// is what is seen - and they are not interchangeable: a viewmodel is rigged and animated for the
	/// eye, a world model for the hand.
	/// </summary>
	private static readonly Dictionary<string, string> FromTheEye = new()
	{
		["models/weapons/sbox_pistol_usp/w_usp.vmdl"] = "models/weapons/sbox_pistol_usp/v_usp.vmdl",
		["models/weapons/sbox_smg_mp5/w_mp5.vmdl"] = "models/weapons/sbox_smg_mp5/v_mp5.vmdl",
		["models/weapons/sbox_assault_m4a1/w_m4a1.vmdl"] = "models/weapons/sbox_assault_m4a1/v_m4a1.vmdl",
	};

	/// <summary>Where the model sits, in the camera's own frame: forward, right, up.</summary>
	private static readonly Vector3 Hold = new( 26f, 7f, -7f );

	private GameObject _shown;
	private string _model;

	public ViewModel( Scene scene ) : base( scene )
	{
		Listen( Stage.StartUpdate, 1, Update, nameof( ViewModel ) );
	}

	private void Update()
	{
		var pawn = Hexagon.Player.Local;

		// Nothing to show unless this client has a pawn, in a body, looking out of its eyes.
		if ( pawn is null || Scene.Camera is null )
		{
			Clear();
			return;
		}

		if ( pawn.GetComponent<PlayerController>( true ) is not { } controller || controller.ThirdPerson )
		{
			// Third person: the body is drawn, and the gun on its hand with it. A first-person model
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
			var renderer = _shown.AddComponent<ModelRenderer>();
			renderer.Model = Model.Load( eyeModel );
			_model = eyeModel;
		}

		// Placed in the camera's frame rather than the world's, so it holds its position whichever way
		// the eye is turned.
		var rotation = Scene.Camera.WorldRotation;
		_shown.WorldRotation = rotation;
		_shown.WorldPosition = Scene.Camera.WorldPosition + rotation.Forward * Hold.x + rotation.Right * Hold.y + rotation.Up * Hold.z;
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
	}
}
