using UnityEngine;
using Mirror;
using System.Collections;
using System.Collections.Generic;
using System.Linq;

public class AccurateMirrorGridGeneration : NetworkBehaviour 
{
    #region GameObjects
    [Header("Assign Prefab here")]
    public GameObject blockGameObject;
    #endregion

    #region Variables
    [SerializeField] private int worldSizeX = 10;
    [SerializeField] private int worldSizeZ = 10;
    [SerializeField] private int noiseHeight = 3;
    [SerializeField] private int activeRadius = 15;
    [SerializeField] private float cleanupInterval = 0.5f;
    [SerializeField] private float gridSyncInterval = 0.1f;
    
    // IMPORTANT: Seed for consistent noise generation
    [SyncVar] private int noiseSeed = 12345;
    #endregion

    #region Data Containers
    private Vector3 startPosition;
    
    // Client-side platform storage
    private Dictionary<Vector2Int, GameObject> activePlatforms = new Dictionary<Vector2Int, GameObject>();
    private Queue<GameObject> inactivePlatformPool = new Queue<GameObject>();
    
    // Server-side player tracking
    private List<Transform> allPlayerTransforms = new List<Transform>();
    private Dictionary<Transform, Vector2Int> lastPlayerGridPositions = new Dictionary<Transform, Vector2Int>();
    
    // Network synchronized active platforms - this is the master list
    private readonly SyncList<SerializableVector2Int> activePlatformPositions = new SyncList<SerializableVector2Int>();
    #endregion

    #region Serializable Types
    [System.Serializable]
    public struct SerializableVector2Int : System.IEquatable<SerializableVector2Int>
    {
        public int x;
        public int z;

        public SerializableVector2Int(int x, int z)
        {
            this.x = x;
            this.z = z;
        }

        public SerializableVector2Int(Vector2Int vec)
        {
            this.x = vec.x;
            this.z = vec.y;
        }

        public Vector2Int ToVector2Int()
        {
            return new Vector2Int(x, z);
        }

        public bool Equals(SerializableVector2Int other)
        {
            return x == other.x && z == other.z;
        }

        public override bool Equals(object obj)
        {
            return obj is SerializableVector2Int other && Equals(other);
        }

        public override int GetHashCode()
        {
            return System.HashCode.Combine(x, z);
        }
    }
    #endregion

    void Start()
    {
        startPosition = transform.position;
        
        // Set consistent seed for noise generation
        if (isServer)
        {
            noiseSeed = Random.Range(0, 999999);
        }
        
        if (isServer)
        {
            StartCoroutine(ServerGridManagement());
            InvokeRepeating(nameof(UpdatePlayerList), 0.5f, 0.5f);
        }
        
        // Listen for platform position changes
        activePlatformPositions.Callback += OnActivePlatformsChanged;
        
        // Generate initial platforms based on current active positions
        StartCoroutine(InitialClientSync());
    }

    IEnumerator InitialClientSync()
    {
        // Wait a frame to ensure SyncVars are received
        yield return new WaitForEndOfFrame();
        
        if (!isServer)
        {
            // Create platforms for all positions in the synced set
            foreach (var pos in activePlatformPositions)
            {
                CreateClientPlatform(pos.ToVector2Int());
            }
        }
    }

    void UpdatePlayerList()
    {
        if (!isServer) return;
        
        // Find all active networked players
        allPlayerTransforms.Clear();
        
        foreach (var conn in NetworkServer.connections.Values)
        {
            if (conn.identity != null && conn.identity.gameObject != null)
            {
                // Check if this is a player object (has the Player tag or specific component)
                GameObject playerObj = conn.identity.gameObject;
                if (playerObj.CompareTag("Player"))
                {
                    allPlayerTransforms.Add(playerObj.transform);
                }
            }
        }
        
        Debug.Log($"Tracking {allPlayerTransforms.Count} players for grid generation");
    }

    IEnumerator ServerGridManagement()
    {
        while (true)
        {
            yield return new WaitForSeconds(gridSyncInterval);
            
            if (allPlayerTransforms.Count == 0)
                continue;
                
            bool anyPlayerMoved = CheckPlayerMovement();
            
            if (anyPlayerMoved)
            {
                UpdateGridForAllPlayers();
            }
            
            yield return new WaitForSeconds(cleanupInterval);
            CleanupDistantPlatforms();
        }
    }

    bool CheckPlayerMovement()
    {
        bool anyMoved = false;
        
        foreach (Transform playerTransform in allPlayerTransforms.ToList())
        {
            if (playerTransform == null)
            {
                allPlayerTransforms.Remove(playerTransform);
                lastPlayerGridPositions.Remove(playerTransform);
                continue;
            }
            
            Vector2Int currentGridPos = GetPlayerGridPosition(playerTransform);
            
            if (!lastPlayerGridPositions.ContainsKey(playerTransform))
            {
                lastPlayerGridPositions[playerTransform] = currentGridPos;
                anyMoved = true;
            }
            else if (lastPlayerGridPositions[playerTransform] != currentGridPos)
            {
                lastPlayerGridPositions[playerTransform] = currentGridPos;
                anyMoved = true;
                Debug.Log($"Player moved to grid position: {currentGridPos}");
            }
        }
        
        return anyMoved;
    }

    void UpdateGridForAllPlayers()
    {
        if (!isServer) return;
        
        HashSet<Vector2Int> requiredPlatforms = new HashSet<Vector2Int>();
        
        // Collect all positions that need platforms based on all players
        foreach (Transform playerTransform in allPlayerTransforms)
        {
            if (playerTransform == null) continue;
            
            Vector2Int playerGridPos = GetPlayerGridPosition(playerTransform);
            
            for (int x = -worldSizeX; x < worldSizeX; x++)
            {
                for (int z = -worldSizeZ; z < worldSizeZ; z++)
                {
                    Vector2Int gridPos = new Vector2Int(x + playerGridPos.x, z + playerGridPos.y);
                    requiredPlatforms.Add(gridPos);
                }
            }
        }
        
        // Add new platforms to the synced list
        foreach (Vector2Int gridPos in requiredPlatforms)
        {
            SerializableVector2Int serializablePos = new SerializableVector2Int(gridPos);
            if (!activePlatformPositions.Contains(serializablePos))
            {
                activePlatformPositions.Add(serializablePos);
                Debug.Log($"Added platform at {gridPos}");
            }
        }
    }

    void CleanupDistantPlatforms()
    {
        if (!isServer) return;
        
        List<SerializableVector2Int> platformsToRemove = new List<SerializableVector2Int>();
        
        foreach (var serializablePos in activePlatformPositions)
        {
            Vector2Int platformGridPos = serializablePos.ToVector2Int();
            bool shouldKeep = false;
            
            // Check if platform is within active radius of any player
            foreach (Transform playerTransform in allPlayerTransforms)
            {
                if (playerTransform == null) continue;
                
                Vector2Int playerGridPos = GetPlayerGridPosition(playerTransform);
                float distance = Vector2Int.Distance(platformGridPos, playerGridPos);
                
                if (distance <= activeRadius)
                {
                    shouldKeep = true;
                    break;
                }
            }
            
            if (!shouldKeep)
            {
                platformsToRemove.Add(serializablePos);
            }
        }
        
        // Remove distant platforms from synced list
        foreach (var pos in platformsToRemove)
        {
            activePlatformPositions.Remove(pos);
            Debug.Log($"Removed platform at {pos.ToVector2Int()}");
        }
    }

    // Handle changes to the synced platform positions
    void OnActivePlatformsChanged(SyncList<SerializableVector2Int>.Operation op, int index, SerializableVector2Int oldItem, SerializableVector2Int newItem)
    {
        switch (op)
        {
            case SyncList<SerializableVector2Int>.Operation.OP_ADD:
                CreateClientPlatform(newItem.ToVector2Int());
                break;
                
            case SyncList<SerializableVector2Int>.Operation.OP_REMOVEAT:
                RemoveClientPlatform(oldItem.ToVector2Int());
                break;
                
            case SyncList<SerializableVector2Int>.Operation.OP_CLEAR:
                ClearAllClientPlatforms();
                break;
        }
    }

    void CreateClientPlatform(Vector2Int gridPos)
    {
        if (activePlatforms.ContainsKey(gridPos))
            return;
        
        GameObject platform;
        
        if (inactivePlatformPool.Count > 0)
        {
            platform = inactivePlatformPool.Dequeue();
            platform.SetActive(true);
        }
        else
        {
            platform = Instantiate(blockGameObject, Vector3.zero, Quaternion.identity);
            platform.transform.SetParent(this.transform);
        }
        
        // Use the SAME noise generation with consistent seed
        float height = GenerateDeterministicNoise(gridPos.x, gridPos.y, 8f) * noiseHeight;
        Vector3 worldPos = new Vector3(
            gridPos.x * 1 + startPosition.x,
            height,
            gridPos.y * 1 + startPosition.z
        );
        
        platform.transform.position = worldPos;
        activePlatforms[gridPos] = platform;
    }

    void RemoveClientPlatform(Vector2Int gridPos)
    {
        if (activePlatforms.TryGetValue(gridPos, out GameObject platform))
        {
            platform.SetActive(false);
            inactivePlatformPool.Enqueue(platform);
            activePlatforms.Remove(gridPos);
        }
    }

    void ClearAllClientPlatforms()
    {
        foreach (var platform in activePlatforms.Values)
        {
            platform.SetActive(false);
            inactivePlatformPool.Enqueue(platform);
        }
        activePlatforms.Clear();
    }

    Vector2Int GetPlayerGridPosition(Transform playerTransform)
    {
        return new Vector2Int(
            (int)Mathf.Floor(playerTransform.position.x),
            (int)Mathf.Floor(playerTransform.position.z)
        );
    }

    // CRITICAL: Deterministic noise generation using synced seed
    public float GenerateDeterministicNoise(int x, int z, float detailScale)
    {
        // Use the synced seed to ensure all clients generate identical noise
        Random.State oldState = Random.state;
        Random.InitState(noiseSeed + x * 1000 + z); // Unique seed per coordinate
        
        float XNoise = (x + this.transform.position.y) / detailScale;
        float ZNoise = (z + this.transform.position.z) / detailScale;
        
        float result = Mathf.PerlinNoise(XNoise, ZNoise);
        
        Random.state = oldState; // Restore random state
        return result;
    }

    // Legacy method for compatibility (uses deterministic version)
    public float generateNoise(int x, int z, float detailScale)
    {
        return GenerateDeterministicNoise(x, z, detailScale);
    }

    // Debug methods
    [Command(requiresAuthority = false)]
    void CmdDebugPlatformCount(NetworkConnectionToClient sender = null)
    {
        Debug.Log($"Server has {activePlatformPositions.Count} active platforms");
        TargetDebugPlatformCount(sender, activePlatformPositions.Count);
    }

    [TargetRpc]
    void TargetDebugPlatformCount(NetworkConnection target, int serverCount)
    {
        Debug.Log($"Client has {activePlatforms.Count} platforms, server has {serverCount}");
    }

    #region Debug Info
    void OnDrawGizmosSelected()
    {
        if (!Application.isPlaying) return;
        
        // Draw active radius around all players
        Gizmos.color = Color.green;
        foreach (Transform playerTransform in allPlayerTransforms)
        {
            if (playerTransform != null)
            {
                Vector3 playerPos = playerTransform.position;
                Gizmos.DrawWireSphere(playerPos, activeRadius);
                
                // Draw generation area
                Gizmos.color = Color.yellow;
                Gizmos.DrawWireCube(playerPos, new Vector3(worldSizeX * 2, 0, worldSizeZ * 2));
            }
        }
        
        // Draw platform positions
        Gizmos.color = Color.red;
        foreach (var pos in activePlatformPositions)
        {
            Vector3 worldPos = new Vector3(pos.x + startPosition.x, startPosition.y, pos.z + startPosition.z);
            Gizmos.DrawWireCube(worldPos, Vector3.one * 0.5f);
        }
    }
    #endregion
}