using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using FishNet.Managing.Object;
using FishNet.Managing.Transporting;
using PurrNet;
using Tanks.FishNetRelay;
using Tanks.MirrorRelay;
using Tanks.NgoRelay;
using Tanks.PurrNetRelay;
using Unity.Netcode;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;
using Object = UnityEngine.Object;

namespace Tanks.Editor
{
    /// <summary>
    /// Builds the four netcode versions of Mirror's Tanks example from its scene and prefabs:
    /// Assets/Tanks/&lt;Netcode&gt;/Prefabs/{Tank,Projectile}.prefab and Assets/Tanks/Scenes/Tanks_&lt;Netcode&gt;.unity,
    /// plus the Boot scene, the settings asset and the build scene list. Safe to run again.
    /// </summary>
    public static class TanksSetup
    {
        const string SourceScene = "Assets/Mirror/Examples/Tanks/Scenes/Scene.unity";
        const string SourceTank = "Assets/Mirror/Examples/Tanks/Prefabs/Tank.prefab";
        const string SourceProjectile = "Assets/Mirror/Examples/Tanks/Prefabs/Projectile.prefab";
        const string Root = "Assets/Tanks";
        public const string BootScene = Root + "/Scenes/Boot.unity";
        public const string SettingsAsset = Root + "/Resources/TanksSettings.asset";
        const string PurrNetRules = "Packages/dev.purrnet.purrnet/Defaults/NetworkRules/Unsafe.asset";

        public static string ScenePath(Netcode n) => $"{Root}/Scenes/{TanksSettings.SceneFor(n)}.unity";

        [MenuItem("Tanks/Regenerate netcode scenes and prefabs")]
        public static void Generate()
        {
            Directory.CreateDirectory($"{Root}/Scenes");
            Directory.CreateDirectory($"{Root}/Resources");

            if (AssetDatabase.LoadAssetAtPath<TanksSettings>(SettingsAsset) == null)
                AssetDatabase.CreateAsset(ScriptableObject.CreateInstance<TanksSettings>(), SettingsAsset);

            var boot = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
            EditorSceneManager.SaveScene(boot, BootScene);

            foreach (Netcode n in Enum.GetValues(typeof(Netcode)))
            {
                var (tank, projectile) = MakePrefabs(n);
                MakeScene(n, tank, projectile);
            }

            EditorBuildSettings.scenes = new[] { BootScene }
                .Concat(Enum.GetValues(typeof(Netcode)).Cast<Netcode>().Select(ScenePath))
                .Select(p => new EditorBuildSettingsScene(p, true))
                .ToArray();

            AssetDatabase.SaveAssets();
            Debug.Log("Tanks: generated the Mirror, FishNet, PurrNet and NGO scenes and prefabs");
        }

        public static void GenerateBatch()
        {
            try
            {
                Generate();
                EditorApplication.Exit(0);
            }
            catch (Exception e)
            {
                Debug.LogException(e);
                EditorApplication.Exit(1);
            }
        }

        // ---------------------------------------------------------------- prefabs

        static (GameObject tank, GameObject projectile) MakePrefabs(Netcode n)
        {
            string dir = $"{Root}/{n}/Prefabs";
            Directory.CreateDirectory(dir);

            // projectile
            GameObject p = Unpacked(SourceProjectile);
            var sourceProjectile = p.GetComponent<Mirror.Examples.Tanks.Projectile>();
            var projectileCore = p.AddComponent<ProjectileCore>();
            projectileCore.destroyAfter = sourceProjectile.destroyAfter;
            projectileCore.rigidBody = sourceProjectile.rigidBody;
            projectileCore.force = sourceProjectile.force;
            Object.DestroyImmediate(sourceProjectile);
            StripMirror(p, n);
            switch (n)
            {
                case Netcode.Mirror: p.AddComponent<MirrorProjectile>(); break;
                case Netcode.FishNet:
                    p.AddComponent<FishNet.Object.NetworkObject>();
                    p.AddComponent<FishNetProjectile>();
                    break;
                case Netcode.PurrNet: p.AddComponent<PurrNetProjectile>(); break;
                case Netcode.NGO:
                    p.AddComponent<NetworkObject>();
                    p.AddComponent<NgoProjectile>();
                    break;
            }
            GameObject projectilePrefab = Save(p, $"{dir}/Projectile.prefab");

            // tank
            GameObject t = Unpacked(SourceTank);
            var source = t.GetComponent<Mirror.Examples.Tanks.Tank>();
            var core = t.AddComponent<TankCore>();
            core.agent = source.agent;
            core.animator = source.animator;
            core.healthBar = source.healthBar;
            core.turret = source.turret;
            core.rotationSpeed = source.rotationSpeed;
            core.shootKey = source.shootKey;
            core.projectileMount = source.projectileMount;
            core.projectilePrefab = projectilePrefab;
            Object.DestroyImmediate(source);
            StripMirror(t, n);
            switch (n)
            {
                case Netcode.Mirror:
                    // keeps Mirror's NetworkIdentity and client authoritative NetworkTransform(s)
                    t.AddComponent<MirrorTank>();
                    break;
                case Netcode.FishNet:
                    t.AddComponent<FishNet.Object.NetworkObject>();
                    t.AddComponent<FishNet.Component.Transforming.NetworkTransform>(); // client authoritative by default
                    core.turret.gameObject.AddComponent<FishNet.Component.Transforming.NetworkTransform>();
                    t.AddComponent<FishNetTank>();
                    break;
                case Netcode.PurrNet:
                    t.AddComponent<PurrNet.NetworkTransform>(); // owner authoritative by default
                    core.turret.gameObject.AddComponent<PurrNet.NetworkTransform>();
                    t.AddComponent<PurrNetTank>();
                    break;
                case Netcode.NGO:
                    t.AddComponent<NetworkObject>();
                    // unreliable deltas: transform updates use the relay's UDP channel, like the other netcodes
                    t.AddComponent<OwnerNetworkTransform>().UseUnreliableDeltas = true;
                    core.turret.gameObject.AddComponent<OwnerNetworkTransform>().UseUnreliableDeltas = true;
                    t.AddComponent<NgoTank>();
                    break;
            }
            GameObject tankPrefab = Save(t, $"{dir}/Tank.prefab");
            return (tankPrefab, projectilePrefab);
        }

        static GameObject Unpacked(string path)
        {
            var go = (GameObject)PrefabUtility.InstantiatePrefab(AssetDatabase.LoadAssetAtPath<GameObject>(path));
            PrefabUtility.UnpackPrefabInstance(go, PrefabUnpackMode.Completely, InteractionMode.AutomatedAction);
            return go;
        }

        /// <summary>Removes Mirror's networking components (dependents first), except on the Mirror version.</summary>
        static void StripMirror(GameObject go, Netcode n)
        {
            if (n == Netcode.Mirror) return;
            foreach (var c in go.GetComponentsInChildren<Mirror.NetworkBehaviour>(true)) Object.DestroyImmediate(c);
            foreach (var c in go.GetComponentsInChildren<Mirror.NetworkIdentity>(true)) Object.DestroyImmediate(c);
        }

        static GameObject Save(GameObject instance, string path)
        {
            GameObject prefab = PrefabUtility.SaveAsPrefabAsset(instance, path);
            Object.DestroyImmediate(instance);
            // NGO computes NetworkObject.GlobalObjectIdHash when the prefab asset is validated and saved
            if (prefab.GetComponent<NetworkObject>() != null)
            {
                EditorUtility.SetDirty(prefab);
                PrefabUtility.SavePrefabAsset(prefab);
            }
            return prefab;
        }

        // ---------------------------------------------------------------- scenes

        static void MakeScene(Netcode n, GameObject tank, GameObject projectile)
        {
            Scene scene = EditorSceneManager.OpenScene(SourceScene, OpenSceneMode.Single);

            Object.DestroyImmediate(Object.FindFirstObjectByType<Mirror.NetworkManager>().gameObject);

            List<Transform> spawns = new List<Transform>();
            foreach (var start in Object.FindObjectsByType<Mirror.NetworkStartPosition>(FindObjectsSortMode.InstanceID))
            {
                start.gameObject.AddComponent<TankSpawnPoint>();
                spawns.Add(start.transform);
                if (n != Netcode.Mirror) Object.DestroyImmediate(start);
            }
            spawns = spawns.OrderBy(s => s.name).ThenBy(s => s.position.x).ToList();
            for (int i = 0; i < spawns.Count; i++) spawns[i].name = "Spawn " + (i + 1);

            var go = new GameObject("NetworkManager");
            switch (n)
            {
                case Netcode.Mirror: SetupMirror(go, tank, projectile); break;
                case Netcode.FishNet: SetupFishNet(go, tank, projectile, spawns); break;
                case Netcode.PurrNet: SetupPurrNet(go, tank, projectile, spawns); break;
                case Netcode.NGO: SetupNgo(go, tank, projectile); break;
            }

            EditorSceneManager.SaveScene(scene, ScenePath(n));
        }

        static void SetupMirror(GameObject go, GameObject tank, GameObject projectile)
        {
            var transport = go.AddComponent<MirrorRelayTransport>();
            var nm = go.AddComponent<Mirror.NetworkManager>();
            nm.dontDestroyOnLoad = false;
            nm.playerPrefab = tank;
            nm.spawnPrefabs = new List<GameObject> { projectile };
            var so = new SerializedObject(nm);
            so.FindProperty("transport").objectReferenceValue = transport;
            so.ApplyModifiedPropertiesWithoutUndo();
            go.AddComponent<MirrorSession>();
        }

        static void SetupFishNet(GameObject go, GameObject tank, GameObject projectile, List<Transform> spawns)
        {
            string prefabsPath = $"{Root}/FishNet/SpawnablePrefabs.asset";
            AssetDatabase.DeleteAsset(prefabsPath);
            var prefabs = ScriptableObject.CreateInstance<SinglePrefabObjects>();
            prefabs.AddObjects(new List<FishNet.Object.NetworkObject>
            {
                tank.GetComponent<FishNet.Object.NetworkObject>(),
                projectile.GetComponent<FishNet.Object.NetworkObject>(),
            }, false, false);
            AssetDatabase.CreateAsset(prefabs, prefabsPath);

            var transport = go.AddComponent<FishNetRelayTransport>();
            var tm = go.AddComponent<TransportManager>();
            tm.Transport = transport;
            var nm = go.AddComponent<FishNet.Managing.NetworkManager>();
            nm.SpawnablePrefabs = prefabs;
            var spawner = go.AddComponent<FishNet.Component.Spawning.PlayerSpawner>();
            spawner.SetPlayerPrefab(tank.GetComponent<FishNet.Object.NetworkObject>());
            spawner.Spawns = spawns.ToArray();
            go.AddComponent<FishNetSession>();
        }

        static void SetupPurrNet(GameObject go, GameObject tank, GameObject projectile, List<Transform> spawns)
        {
            string prefabsPath = $"{Root}/PurrNet/NetworkPrefabs.asset";
            AssetDatabase.DeleteAsset(prefabsPath);
            var prefabs = ScriptableObject.CreateInstance<PurrNet.NetworkPrefabs>();
            prefabs.autoGenerate = false;
            prefabs.prefabs = new[] { tank, projectile }.Select(p => new PurrNet.NetworkPrefabs.UserPrefabData
            {
                guid = AssetDatabase.AssetPathToGUID(AssetDatabase.GetAssetPath(p)),
                prefab = p,
            }).ToList();
            AssetDatabase.CreateAsset(prefabs, prefabsPath);

            var transport = go.AddComponent<PurrNetRelayTransport>();
            var nm = go.AddComponent<PurrNet.NetworkManager>();
            nm.startServerFlags = StartFlags.None;
            nm.startClientFlags = StartFlags.None;
            var so = new SerializedObject(nm);
            so.FindProperty("_transport").objectReferenceValue = transport;
            so.FindProperty("_networkPrefabs").objectReferenceValue = prefabs;
            so.FindProperty("_networkRules").objectReferenceValue = AssetDatabase.LoadAssetAtPath<ScriptableObject>(PurrNetRules);
            so.ApplyModifiedPropertiesWithoutUndo();

            var spawner = go.AddComponent<PlayerSpawner>();
            var sso = new SerializedObject(spawner);
            sso.FindProperty("_playerPrefab").objectReferenceValue = tank;
            var points = sso.FindProperty("spawnPoints");
            points.arraySize = spawns.Count;
            for (int i = 0; i < spawns.Count; i++) points.GetArrayElementAtIndex(i).objectReferenceValue = spawns[i];
            sso.ApplyModifiedPropertiesWithoutUndo();
            go.AddComponent<PurrNetSession>();
        }

        static void SetupNgo(GameObject go, GameObject tank, GameObject projectile)
        {
            string prefabsPath = $"{Root}/NGO/NetworkPrefabsList.asset";
            AssetDatabase.DeleteAsset(prefabsPath);
            var list = ScriptableObject.CreateInstance<NetworkPrefabsList>();
            list.Add(new NetworkPrefab { Prefab = tank });
            list.Add(new NetworkPrefab { Prefab = projectile });
            AssetDatabase.CreateAsset(list, prefabsPath);

            var transport = go.AddComponent<NgoRelayTransport>();
            var nm = go.AddComponent<Unity.Netcode.NetworkManager>();
            nm.NetworkConfig = new NetworkConfig
            {
                NetworkTransport = transport,
                PlayerPrefab = tank,
            };
            nm.NetworkConfig.Prefabs.NetworkPrefabsLists = new List<NetworkPrefabsList> { list };
            nm.RunInBackground = true;
            go.AddComponent<NgoSession>();
        }

        // ---------------------------------------------------------------- netcode switch

        [MenuItem("Tanks/Netcode/Mirror")] static void UseMirror() => Use(Netcode.Mirror);
        [MenuItem("Tanks/Netcode/FishNet")] static void UseFishNet() => Use(Netcode.FishNet);
        [MenuItem("Tanks/Netcode/PurrNet")] static void UsePurrNet() => Use(Netcode.PurrNet);
        [MenuItem("Tanks/Netcode/NGO")] static void UseNgo() => Use(Netcode.NGO);

        [MenuItem("Tanks/Netcode/Mirror", true)] static bool CheckMirror() => Check(Netcode.Mirror);
        [MenuItem("Tanks/Netcode/FishNet", true)] static bool CheckFishNet() => Check(Netcode.FishNet);
        [MenuItem("Tanks/Netcode/PurrNet", true)] static bool CheckPurrNet() => Check(Netcode.PurrNet);
        [MenuItem("Tanks/Netcode/NGO", true)] static bool CheckNgo() => Check(Netcode.NGO);

        /// <summary>Selects the netcode in TanksSettings.asset (used by builds and the Boot scene) and opens its scene.</summary>
        static void Use(Netcode n)
        {
            var settings = AssetDatabase.LoadAssetAtPath<TanksSettings>(SettingsAsset);
            settings.netcode = n;
            EditorUtility.SetDirty(settings);
            AssetDatabase.SaveAssets();
            if (EditorSceneManager.SaveCurrentModifiedScenesIfUserWantsTo()) EditorSceneManager.OpenScene(ScenePath(n));
        }

        static bool Check(Netcode n)
        {
            var settings = AssetDatabase.LoadAssetAtPath<TanksSettings>(SettingsAsset);
            Menu.SetChecked("Tanks/Netcode/" + n, settings != null && settings.netcode == n);
            return true;
        }
    }
}
