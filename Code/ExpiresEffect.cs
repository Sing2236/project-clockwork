using Sandbox;

namespace Hl2rp;

/// <summary>
/// Removes its object once <see cref="Seconds"/> have passed.
///
/// <para>Small enough to be worth having only because the engine has no equivalent: GameObject offers
/// Destroy and DestroyImmediate and nothing that takes a delay, so a flash that should last one shot
/// would otherwise have to be tracked and cleaned up by whatever made it.</para>
/// </summary>
public sealed class ExpiresEffect : Component
{
	/// <summary>How long it lives. A muzzle flash wants a fiftieth of a second or so.</summary>
	public float Seconds { get; set; } = 0.05f;

	private TimeSince _born;

	protected override void OnStart() => _born = 0;

	protected override void OnUpdate()
	{
		if ( _born >= Seconds ) GameObject.Destroy();
	}
}
