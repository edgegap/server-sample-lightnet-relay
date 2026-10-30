using System.Collections.Generic;
using UnityEngine;
using UnityEngine.AI;

namespace Tanks
{
    /// <summary>The network side of a tank, implemented once per netcode (MirrorTank, FishNetTank...).</summary>
    public interface ITankNet
    {
        bool IsLocalOwner { get; }
        bool IsServer { get; }
        bool IsSpawned { get; }
        int Health { get; }
        /// <summary>Owner's connection id, picks the spawn point.</summary>
        int OwnerId { get; }
        /// <summary>Owner asks the server to fire.</summary>
        void RequestFire();
        /// <summary>Server: a projectile hit this tank.</summary>
        void ServerTakeHit();
    }

    /// <summary>
    /// The Tanks gameplay of Mirror's example, without any netcode: input, movement, turret, health bar, hits.
    /// The netcode component next to it (<see cref="ITankNet"/>) syncs it.
    /// </summary>
    public class TankCore : MonoBehaviour
    {
        public const int MaxHealth = 4;
        public static readonly List<TankCore> All = new List<TankCore>();

        [Header("Components")]
        public NavMeshAgent agent;
        public Animator animator;
        public TextMesh healthBar;
        public Transform turret;

        [Header("Movement")]
        public float rotationSpeed = 100;

        [Header("Firing")]
        public KeyCode shootKey = KeyCode.Space;
        public GameObject projectilePrefab;
        public Transform projectileMount;

        /// <summary>Test input, set by the e2e driver: drive forward / fire without a keyboard.</summary>
        public static bool AutoDrive, AutoFire;

        public ITankNet Net { get; private set; }
        public Vector3 SpawnPosition { get; private set; }
        public bool IsLocal => Net != null && Net.IsLocalOwner;

        bool placed;
        float nextAutoFire;

        void Awake()
        {
            Net = GetComponent<ITankNet>();
        }

        void OnEnable() => All.Add(this);
        void OnDisable() => All.Remove(this);

        void Update()
        {
            if (Net == null || !Net.IsSpawned) return;
            if (!placed) Place();

            healthBar.text = new string('-', Mathf.Max(Net.Health, 0));
            if (!IsLocal) return;

            // rotate
            // auto drive goes round in circles so it never stops at a wall
            float horizontal = AutoDrive ? 0.3f : Input.GetAxis("Horizontal");
            transform.Rotate(0, horizontal * rotationSpeed * Time.deltaTime, 0);

            // move
            float vertical = AutoDrive ? 1 : Input.GetAxis("Vertical");
            Vector3 forward = transform.TransformDirection(Vector3.forward);
            Vector3 velocity = forward * Mathf.Max(vertical, 0) * agent.speed;
            // Move() applies right away and works without a keyboard in batch mode
            if (agent.isOnNavMesh) agent.Move(velocity * Time.deltaTime);
            animator.SetBool("Moving", velocity != Vector3.zero);

            // shoot
            bool fire = Input.GetKeyDown(shootKey);
            if (AutoFire && Time.time >= nextAutoFire)
            {
                fire = true;
                nextAutoFire = Time.time + 1;
            }
            if (fire) Net.RequestFire();

            RotateTurret();
        }

        void Place()
        {
            placed = true;
            if (IsLocal)
            {
                // every netcode spawns players differently; the owner moves its own tank to a spawn point
                // and the (owner authoritative) network transform syncs it
                Transform spawn = TankSpawnPoint.Pick(Net.OwnerId);
                if (spawn != null)
                {
                    if (agent.isOnNavMesh) agent.Warp(spawn.position);
                    else transform.position = spawn.position;
                    transform.rotation = spawn.rotation;
                }
            }
            else
            {
                // remote tanks follow their network transform; the agent would fight it
                agent.enabled = false;
            }
            SpawnPosition = transform.position;
        }

        /// <summary>Called on every machine when this tank fires (from the netcode's RPC).</summary>
        public void PlayShoot() => animator.SetTrigger("Shoot");

        /// <summary>Server: create the projectile instance the netcode spawns.</summary>
        public GameObject CreateProjectile() => Instantiate(projectilePrefab, projectileMount.position, projectileMount.rotation);

        void OnTriggerEnter(Collider other)
        {
            if (Net != null && Net.IsServer && other.GetComponent<ProjectileCore>() != null)
            {
                Net.ServerTakeHit();
            }
        }

        void RotateTurret()
        {
            if (Camera.main == null) return;
            Ray ray = Camera.main.ScreenPointToRay(Input.mousePosition);
            if (Physics.Raycast(ray, out RaycastHit hit, 100))
            {
                Vector3 lookRotation = new Vector3(hit.point.x, turret.transform.position.y, hit.point.z);
                turret.transform.LookAt(lookRotation);
            }
        }
    }
}
