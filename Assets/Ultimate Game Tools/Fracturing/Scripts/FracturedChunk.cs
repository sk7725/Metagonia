using UnityEngine;
using System;
using System.Collections;
using System.Collections.Generic;
using UltimateFracturing;

[ExecuteInEditMode, Serializable]
public class FracturedChunk : MonoBehaviour
{
    [Serializable]
    public class AdjacencyInfo
    {
        public AdjacencyInfo(FracturedChunk chunk, float fArea)
        {
            this.chunk = chunk;
            this.fArea = fArea;
        }

        public FracturedChunk chunk; // The connected chunk
        public float          fArea; // The connected surface area
    }

    public class CollisionInfo
    {
        public CollisionInfo(FracturedChunk chunk, Collision collisionInfo, bool bIsMain)
        {
            this.chunk            = chunk;
            this.collisionInfo    = collisionInfo;
            this.bIsMain          = bIsMain;
            bCancelCollisionEvent = false;
        }

        public FracturedChunk chunk;                  // The chunk that received the collision and is going to detach
        public Collision      collisionInfo;          // The collision information
        public bool           bIsMain;                // For non detached chunks, is it the main chunk (the one that was hit) or is it a neighbour one that went also with it (due to FracturedObject.ChunkConnectionStrength being < 1)
        public bool           bCancelCollisionEvent;  // User can cancel the collision event so that it won't generate anything
    }

    public FracturedObject FracturedObjectSource;
    public int             SplitSubMeshIndex          = -1;
    public bool            DontDeleteAfterBroken      = false;
    public bool            IsSupportChunk             = false;       // Is it a chunk that acts as support?
    public bool            IsNonSupportedChunk        = false;       // It it a chunk that is not supported at this moment?
    public bool            IsDetachedChunk            = false;       // Is it a chunk that has been detached?
    public float           RelativeVolume             = 0.01f;       // We use this to compute the mass. The mass of this chunk is ObjectTotalMass * chunk.RelativeVolume
    public float           Volume                     = 0.0f;
    public bool            HasConcaveCollider         = false;       // Does it have a collider mesh generated by Concave Collider?
    public float           PreviewDecompositionValue  = 0.0f;
    public Color           RandomMaterialColor        = Color.white; // For FracturedObject.EnableRandomColoredChunks()
    public bool            Visited                    = false;       // Visited flag for recursive calls.

    public List<AdjacencyInfo> ListAdjacentChunks = new List<AdjacencyInfo>();
    
    [SerializeField] private Vector3    m_v3InitialLocalPosition;
    [SerializeField] private Quaternion m_qInitialLocalRotation;
    [SerializeField] private Vector3    m_v3InitialLocalScale;
    [SerializeField] private bool       m_bInitialLocalRotScaleInitialized = false;

    private List<AdjacencyInfo> ListAdjacentChunksCopy;
    private float m_fInvisibleTimer;
    private bool  m_bNonSupportedChunkStored;

    void Awake()
    {
        if(Application.isPlaying)
        {
            IsDetachedChunk         = false;
            transform.localPosition = m_v3InitialLocalPosition;

            if(m_bInitialLocalRotScaleInitialized)
            {
                transform.localRotation = m_qInitialLocalRotation;
                transform.localScale    = m_v3InitialLocalScale;
            }

            ListAdjacentChunksCopy  = new List<AdjacencyInfo>(ListAdjacentChunks);
            m_fInvisibleTimer       = 0.0f;
        }

        m_bNonSupportedChunkStored = IsNonSupportedChunk;
    }

    void Update()
    {
        if(Application.isPlaying)
        {
            if(GetComponent<Renderer>().isVisible == false && IsDetachedChunk == true)
            {
                m_fInvisibleTimer += Time.deltaTime;

                if(FracturedObjectSource != null)
                {
                    if(m_fInvisibleTimer > FracturedObjectSource.EventDetachedOffscreenLifeTime)
                    {
                        Destroy(this.gameObject);
                    }
                }
            }
            else
            {
                m_fInvisibleTimer = 0.0f;
            }
        }
    }

    void OnCollisionEnter(Collision collision)
    {
        if(FracturedObjectSource == null || collision == null)
        {
            return;
        }
        
        if(collision.contacts == null)
        {
            return;
        }

        if(collision.contacts.Length == 0)
        {
            return;
        }

        if(collision.gameObject)
        {
            FracturedChunk otherChunk = collision.gameObject.GetComponent<FracturedChunk>();

            if(otherChunk)
            {
                if(otherChunk.GetComponent<Rigidbody>().isKinematic && IsDetachedChunk == false)
                {
                    // Just intersecting with other chunk in kinematic state
                    return;
                }
            }
        }

        float fMass = Mathf.Infinity; // If there is no rigidbody we consider it static

        if(collision.rigidbody)
        {
            fMass = collision.rigidbody.mass;
        }

        if(IsDetachedChunk == false)
        {
            // Chunk still attached.
            // We are going to check if the collision is against a free chunk of the same object. This way we prevent chunks pushing each other out, we want to control
            // this only through the FractureObject.InterconnectionStrength variable

            bool bOtherIsFreeChunkFromSameObject = false;

            FracturedChunk otherChunk = collision.gameObject.GetComponent<FracturedChunk>();

            if(otherChunk != null)
            {
                if(otherChunk.IsDetachedChunk == true && otherChunk.FracturedObjectSource == FracturedObjectSource)
                {
                    bOtherIsFreeChunkFromSameObject = true;
                }
            }
            
            Debug.Log("Chunk Collision: " + collision.gameObject.name);
            
            if(bOtherIsFreeChunkFromSameObject == false && collision.relativeVelocity.magnitude > FracturedObjectSource.EventDetachMinVelocity && fMass > FracturedObjectSource.EventDetachMinMass && GetComponent<Rigidbody>() != null && IsDestructibleChunk())
            {
                CollisionInfo collisionInfo = new CollisionInfo(this, collision, true);
                if (!Check(collision)) return;
                
                FracturedObjectSource.NotifyChunkCollision(collisionInfo);
                if (!CheckNumDetachedFragment()) return;
                
                FracturedObjectSource.NotifyDetachChunkCollision(collisionInfo);

                if(collisionInfo.bCancelCollisionEvent == false)
                {
                    List<FracturedChunk> listBreaks = new List<FracturedChunk>();

                    // Impact enough to make it detach. Compute random list of connected chunks that are detaching as well (we'll use the ConnectionStrength parameter).
                    listBreaks = ComputeRandomConnectionBreaks();
                    listBreaks.Add(this);
                    DetachFromObject();

                    foreach(FracturedChunk chunk in listBreaks)
                    {
                        collisionInfo.chunk = chunk;
                        collisionInfo.bIsMain = false;
                        collisionInfo.bCancelCollisionEvent = false;

                        if(chunk != this)
                        {
                            FracturedObjectSource.NotifyDetachChunkCollision(collisionInfo);
                        }

                        if(collisionInfo.bCancelCollisionEvent == false)
                        {
                            chunk.DetachFromObject();
                            chunk.GetComponent<Rigidbody>().AddExplosionForce(collision.relativeVelocity.magnitude * FracturedObjectSource.EventDetachExitForce, collision.contacts[0].point, 0.0f, FracturedObjectSource.EventDetachUpwardsModifier);
                        }
                    }
                }
            }
        }
        else
        {
            // Free chunk

            if(collision.relativeVelocity.magnitude > FracturedObjectSource.EventDetachedMinVelocity && fMass > FracturedObjectSource.EventDetachedMinMass)
            {
                FracturedObjectSource.NotifyFreeChunkCollision(new CollisionInfo(this, collision, true));
            }
        }
    }

#if UNITY_EDITOR

    void OnRenderObject()
    {
        if(FracturedObjectSource == null)
        {
            return;
        }

        if(FracturedObjectSource.ShowChunkColoredState == false && FracturedObjectSource.ShowChunkColoredRandomly == false)
        {
            return;
        }

        bool bSourceSelected = false;

        foreach(GameObject selection in UnityEditor.Selection.gameObjects)
        {
            if(selection == FracturedObjectSource.gameObject)
            {
                bSourceSelected = true;
                break;
            }
        }

        if(bSourceSelected == false)
        {
            return;
        }

        // Draw support mesh on the editor window.
        // We use this approach instead of Handles.xxx because it integrates better (depth test with other objects, our own shader etc.).

        if(UnityEditor.SceneView.lastActiveSceneView)
        {
            if(Application.isPlaying == false && Camera.current == UnityEditor.SceneView.lastActiveSceneView.camera)
            {
                MeshFilter meshFilter = GetComponent<MeshFilter>();

                if(FracturedObjectSource.ShowChunkColoredRandomly)
                {
                    if(meshFilter)
                    {
                        if(meshFilter.sharedMesh)
                        {
                            FracturedObjectSource.GizmosMaterial.SetColor("_Color", RandomMaterialColor);
                            FracturedObjectSource.GizmosMaterial.SetPass(0);
                            Graphics.DrawMeshNow(meshFilter.sharedMesh, transform.localToWorldMatrix);
                        }
                    }
                }
                else if(FracturedObjectSource.ShowChunkColoredState)
                {
                    if(IsSupportChunk && meshFilter)
                    {
                        if(meshFilter.sharedMesh)
                        {
                            FracturedObjectSource.GizmosMaterial.SetColor("_Color", FracturedObject.GizmoColorSupport);
                            FracturedObjectSource.GizmosMaterial.SetPass(0);
                            Graphics.DrawMeshNow(meshFilter.sharedMesh, transform.localToWorldMatrix);
                        }
                    }
                    else if(IsNonSupportedChunk && meshFilter)
                    {
                        if(meshFilter.sharedMesh)
                        {
                            FracturedObjectSource.GizmosMaterial.SetColor("_Color", FracturedObject.GizmoColorNonSupport);
                            FracturedObjectSource.GizmosMaterial.SetPass(0);
                            Graphics.DrawMeshNow(meshFilter.sharedMesh, transform.localToWorldMatrix);
                        }
                    }
                }
            }
        }
    }

#endif

    public bool IsDestructibleChunk()
    {
        if(FracturedObjectSource != null)
        {
            if(FracturedObjectSource.SupportChunksAreIndestructible == true)
            {
                return IsSupportChunk == false;
            }

            if(FracturedObjectSource.SupportChunksAreIndestructible == false)
            {
                return true;
            }
        }

        return IsSupportChunk == false;
    }

    public void ResetChunk(FracturedObject fracturedObjectSource)
    {
        transform.parent      = fracturedObjectSource.transform;
        GetComponent<Rigidbody>().isKinematic = true;
        IsNonSupportedChunk   = m_bNonSupportedChunkStored;

        FracturedObjectSource   = fracturedObjectSource;
        IsDetachedChunk         = false;
        transform.localPosition = m_v3InitialLocalPosition;

        if(m_bInitialLocalRotScaleInitialized)
        {
            transform.localRotation = m_qInitialLocalRotation;
            transform.localScale    = m_v3InitialLocalScale;
        }

        ListAdjacentChunks      = new List<AdjacencyInfo>(ListAdjacentChunksCopy);
        m_fInvisibleTimer       = 0.0f;
    }

    public void Impact(Vector3 v3Position, float fExplosionForce, float fRadius, bool bAlsoImpactFreeChunks)
    {
        if(GetComponent<Rigidbody>() != null && IsDestructibleChunk())
        {
            List<FracturedChunk> listBreaks = new List<FracturedChunk>();

            if(IsDetachedChunk == false)
            {
                // Compute random list of connected chunks that are detaching as well (we'll use the ConnectionStrength parameter).
                listBreaks = ComputeRandomConnectionBreaks();
                listBreaks.Add(this);
                DetachFromObject();

                foreach(FracturedChunk chunk in listBreaks)
                {
                    chunk.DetachFromObject();
                    chunk.GetComponent<Rigidbody>().AddExplosionForce(fExplosionForce, v3Position, 0.0f, 0.0f);
                }
            }

            List<FracturedChunk> listRadius = FracturedObjectSource.GetDestructibleChunksInRadius(v3Position, fRadius, bAlsoImpactFreeChunks);

            foreach(FracturedChunk chunk in listRadius)
            {
                chunk.DetachFromObject();
                chunk.GetComponent<Rigidbody>().AddExplosionForce(fExplosionForce, v3Position, 0.0f, FracturedObjectSource.EventDetachUpwardsModifier);
            }
        }

        // Even if it is support chunk, play the sound and instance the prefabs

        FracturedObjectSource.NotifyImpact(v3Position);
    }

    public void OnCreateFromFracturedObject(FracturedObject fracturedComponent, int nSplitSubMeshIndex)
    {
        FracturedObjectSource    = fracturedComponent;
        SplitSubMeshIndex        = nSplitSubMeshIndex;
        RandomMaterialColor      = new Color(UnityEngine.Random.value, UnityEngine.Random.value, UnityEngine.Random.value, 0.7f);
        m_v3InitialLocalPosition = transform.localPosition;
        m_qInitialLocalRotation  = transform.localRotation;
        m_v3InitialLocalScale    = transform.localScale;

        m_bInitialLocalRotScaleInitialized = true;
    }

    public void UpdatePreviewDecompositionPosition()
    {
        float fMultiplyRadius = 5.0f;
        float fModulateRadius = 1.0f;

        if(FracturedObjectSource != null)
        {
            // This will make objects closer to the center go out with less radius
            fModulateRadius = (m_v3InitialLocalPosition.magnitude / FracturedObjectSource.DecomposeRadius);
        }

        Vector3 v3RadialVector  = m_v3InitialLocalPosition.normalized;
        transform.localPosition = m_v3InitialLocalPosition + (v3RadialVector * (PreviewDecompositionValue * fModulateRadius * fMultiplyRadius));
    }

    public void ConnectTo(FracturedChunk chunk, float fArea)
    {
        if(chunk)
        {
            if(chunk.IsConnectedTo(this))
            {
                return;
            }

            ListAdjacentChunks.Add(new AdjacencyInfo(chunk, fArea));
            chunk.ListAdjacentChunks.Add(new AdjacencyInfo(this, fArea));
        }            
    }

    public void DisconnectFrom(FracturedChunk chunk)
    {
        if(chunk)
        {
            if(chunk.IsConnectedTo(this))
            {
                for(int i = 0; i < ListAdjacentChunks.Count; i++)
                {
                    if(ListAdjacentChunks[i].chunk == chunk)
                    {
                        ListAdjacentChunks.RemoveAt(i);
                        break;
                    }
                }

                for(int i = 0; i < chunk.ListAdjacentChunks.Count; i++)
                {
                    if(chunk.ListAdjacentChunks[i].chunk == this)
                    {
                        chunk.ListAdjacentChunks.RemoveAt(i);
                        break;
                    }
                }
            }
        }            
    }

    public bool IsConnectedTo(FracturedChunk chunk)
    {
        foreach(AdjacencyInfo info in ListAdjacentChunks)
        {
            bool bHasMinArea = true;

            if(info.chunk.FracturedObjectSource)
            {
                bHasMinArea = info.fArea > info.chunk.FracturedObjectSource.ChunkConnectionMinArea;
            }

            if(info.chunk == chunk)
            {
                return bHasMinArea;
            }
        }

        return false;
    }

    public void DetachFromObject(bool bCheckStructureIntegrity = true)
    {
        if(IsDestructibleChunk() && IsDetachedChunk == false && GetComponent<Rigidbody>())
        {
            m_bNonSupportedChunkStored = IsNonSupportedChunk;

            transform.parent      = null;
            gameObject.layer = LayerMask.NameToLayer("Ignore Collision");
            GetComponent<Rigidbody>().isKinematic = false;
            IsDetachedChunk       = true;
            IsNonSupportedChunk   = true;

            RemoveConnectionInfo();

            if(FracturedObjectSource)
            {
                FracturedObjectSource.NotifyChunkDetach(this);

                if(bCheckStructureIntegrity)
                {
                    // Check if we created isolated chunks in the air, not connected to any support chunks
                    FracturedObjectSource.CheckDetachNonSupportedChunks();
                }
            }

            // Check if we need to add a destruction timer
            if(DontDeleteAfterBroken == false && FracturedObjectSource != null)
            {
                DieTimer dieTimer = gameObject.AddComponent<DieTimer>();
                dieTimer.SecondsToDie = UnityEngine.Random.Range(FracturedObjectSource.EventDetachedMinLifeTime, FracturedObjectSource.EventDetachedMaxLifeTime);
            }
        }
    }

    private void RemoveConnectionInfo()
    {
        // Update connection information

        foreach(FracturedChunk.AdjacencyInfo adjacency in ListAdjacentChunks)
        {
            if(adjacency.chunk)
            {
                foreach(FracturedChunk.AdjacencyInfo adjacencyOther in adjacency.chunk.ListAdjacentChunks)
                {
                    if(adjacencyOther.chunk == this)
                    {
                        adjacency.chunk.ListAdjacentChunks.Remove(adjacencyOther);
                        break;
                    }
                }
            }
        }

        ListAdjacentChunks.Clear();
    }

    public List<FracturedChunk> ComputeRandomConnectionBreaks()
    {
        List<FracturedChunk> listBreaks = new List<FracturedChunk>();

        if(FracturedObjectSource == null)
        {
            return listBreaks;
        }

        FracturedObjectSource.ResetAllChunkVisitedFlags();
        ComputeRandomConnectionBreaksRecursive(this, listBreaks, 1);

        return listBreaks;
    }

    private static void ComputeRandomConnectionBreaksRecursive(FracturedChunk chunk, List<FracturedChunk> listBreaksOut, int nLevel)
    {
        if(chunk.Visited == true)
        {
            return;
        }

        chunk.Visited = true;

        foreach(FracturedChunk.AdjacencyInfo adjacency in chunk.ListAdjacentChunks)
        {
            if(adjacency.chunk)
            {
                if(chunk.FracturedObjectSource != null && adjacency.chunk.Visited == false && adjacency.chunk.IsDestructibleChunk())
                {
                    bool bConnected = adjacency.fArea > chunk.FracturedObjectSource.ChunkConnectionMinArea;

                    if(bConnected)
                    {
                        float fRandom = UnityEngine.Random.value;

                        if(fRandom > (chunk.FracturedObjectSource.ChunkConnectionStrength * nLevel))
                        {
                            ComputeRandomConnectionBreaksRecursive(adjacency.chunk, listBreaksOut, nLevel + 1);
                            listBreaksOut.Add(adjacency.chunk);
                        }
                    }
                }
            }
        }
    }

    public static FracturedChunk ChunkRaycast(Vector3 v3Pos, Vector3 v3Forward, out RaycastHit hitInfo)
    {
        FracturedChunk chunk = null;

        if(Physics.Raycast(v3Pos, v3Forward, out hitInfo))
        {
            // Intersection found, try to check if it has a FracturedChunk component

            chunk = hitInfo.collider.GetComponent<FracturedChunk>();

            if(chunk == null && hitInfo.collider.transform.parent != null)
            {
                // Not found, but concave collider creates child nodes, we have to take this into account as well.
                // In this case the FracturedChunk should be in its parent
                chunk = hitInfo.collider.transform.parent.GetComponent<FracturedChunk>();
            }
        }

        return chunk;
    }
    
    private bool Check(Collision collision)
    {
        var checker = FracturedObjectSource.GetComponent<FragmentManager>();
        return checker.Check(collision);
    }

    private bool CheckNumDetachedFragment()
    {
        var checker = FracturedObjectSource.GetComponent<FragmentManager>();
        return checker.CheckNumDetachedFragment();
    }
}
