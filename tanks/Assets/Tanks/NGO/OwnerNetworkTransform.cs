using Unity.Netcode.Components;

namespace Tanks.NgoRelay
{
    /// <summary>Owner authoritative NetworkTransform (NGO's default is server authoritative).</summary>
    public class OwnerNetworkTransform : NetworkTransform
    {
        protected override bool OnIsServerAuthoritative() => false;
    }
}
