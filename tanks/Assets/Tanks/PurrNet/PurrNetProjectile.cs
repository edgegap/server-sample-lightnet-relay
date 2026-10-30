using PurrNet;

namespace Tanks.PurrNetRelay
{
    public class PurrNetProjectile : NetworkBehaviour, IProjectileNet
    {
        public bool IsServer => isServer;
        // PurrNet despawns a spawned object for everyone when the server destroys it
        public void ServerDespawn() => Destroy(gameObject);
    }
}
