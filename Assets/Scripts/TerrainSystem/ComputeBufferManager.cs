using System.Collections.Generic;
using UnityEngine;
using System;

namespace TerrainSystem
{
    /// <summary>
    /// Manages a pool of ComputeBuffer objects to avoid creating them every frame.
    /// </summary>
    public class ComputeBufferManager : MonoBehaviour
    {
        #region Singleton Pattern
        private static ComputeBufferManager _instance;
        public static ComputeBufferManager Instance
        {
            get
            {
                if (_instance == null)
                {
                    GameObject go = new GameObject("ComputeBufferManager");
                    _instance = go.AddComponent<ComputeBufferManager>();
                    DontDestroyOnLoad(go);
                }
                return _instance;
            }
        }

        public static bool TryGetInstance(out ComputeBufferManager manager)
        {
            manager = _instance;
            return manager != null;
        }
        #endregion

        #region Buffer Key Structure
        /// <summary>
        /// Key structure to identify buffer types by count and stride
        /// </summary>
        private readonly struct BufferKey : IEquatable<BufferKey>
        {
            public readonly int Count;
            public readonly int Stride;
            public readonly ComputeBufferType BufferType;

            public BufferKey(int count, int stride, ComputeBufferType bufferType = ComputeBufferType.Default)
            {
                Count = count;
                Stride = stride;
                BufferType = bufferType;
            }

            public bool Equals(BufferKey other)
            {
                return Count == other.Count && 
                       Stride == other.Stride && 
                       BufferType == other.BufferType;
            }

            public override bool Equals(object obj)
            {
                return obj is BufferKey other && Equals(other);
            }

            public override int GetHashCode()
            {
                return HashCode.Combine(Count, Stride, (int)BufferType);
            }
        }
        #endregion

        #region Structured Buffer Cache
        public enum StructuredBufferRole
        {
            Vertex,
            Normal,
            Counter
        }

        private readonly struct PersistentBufferKey : IEquatable<PersistentBufferKey>
        {
            public readonly Vector3Int Resolution;
            public readonly StructuredBufferRole Role;

            public PersistentBufferKey(Vector3Int resolution, StructuredBufferRole role)
            {
                Resolution = resolution;
                Role = role;
            }

            public bool Equals(PersistentBufferKey other)
            {
                return Resolution == other.Resolution && Role == other.Role;
            }

            public override bool Equals(object obj)
            {
                return obj is PersistentBufferKey other && Equals(other);
            }

            public override int GetHashCode()
            {
                unchecked
                {
                    int hash = 17;
                    hash = hash * 31 + Resolution.GetHashCode();
                    hash = hash * 31 + Role.GetHashCode();
                    return hash;
                }
            }
        }

        private sealed class PersistentBufferInfo
        {
            public ComputeBuffer Buffer;
            public int Capacity;
            public int Stride;

            public PersistentBufferInfo(ComputeBuffer buffer, int capacity, int stride)
            {
                Buffer = buffer;
                Capacity = capacity;
                Stride = stride;
            }
        }

        private readonly Dictionary<PersistentBufferKey, PersistentBufferInfo> persistentStructuredBuffers =
            new Dictionary<PersistentBufferKey, PersistentBufferInfo>();

        private static readonly uint[] CounterResetData = new uint[1];
        #endregion

        #region Fields
        // Dictionary to store pools of buffers by size and stride
        private readonly Dictionary<BufferKey, Queue<ComputeBuffer>> bufferPools =
            new Dictionary<BufferKey, Queue<ComputeBuffer>>();

        // Map active buffers to their original keys for precise reuse
        private readonly Dictionary<ComputeBuffer, BufferKey> activeBufferKeys =
            new Dictionary<ComputeBuffer, BufferKey>();

        // Track which buffers are currently in use
        private readonly HashSet<ComputeBuffer> activeBuffers = new HashSet<ComputeBuffer>();

        // Stats for debugging
        public int TotalBuffersCreated { get; private set; }
        public int ActiveBufferCount => activeBuffers.Count;
        public int PooledBufferCount { get; private set; }
        #endregion

        #region Buffer Management Methods
        /// <summary>
        /// Get a ComputeBuffer from the pool or create a new one if none are available.
        /// </summary>
        /// <param name="count">Number of elements in the buffer</param>
        /// <param name="stride">Size in bytes of each element</param>
        /// <param name="type">Type of compute buffer (Default, Counter, etc)</param>
        /// <returns>A ComputeBuffer of the requested size</returns>
        public ComputeBuffer GetBuffer(int count, int stride, ComputeBufferType type = ComputeBufferType.Default)
        {
            BufferKey key = new BufferKey(count, stride, type);
            
            // Create the pool for this type if it doesn't exist
            if (!bufferPools.TryGetValue(key, out Queue<ComputeBuffer> pool))
            {
                pool = new Queue<ComputeBuffer>();
                bufferPools[key] = pool;
            }

            // Get a buffer from the pool or create a new one
            ComputeBuffer buffer;
            if (pool.Count > 0)
            {
                buffer = pool.Dequeue();
                PooledBufferCount--;
            }
            else
            {
                buffer = new ComputeBuffer(count, stride, type);
                TotalBuffersCreated++;
            }
            
            // Mark as active
            activeBuffers.Add(buffer);
            activeBufferKeys[buffer] = key;

            return buffer;
        }

        /// <summary>
        /// Release a buffer back to the pool.
        /// </summary>
        /// <param name="buffer">The buffer to release</param>
        public void ReleaseBuffer(ComputeBuffer buffer)
        {
            if (buffer == null) return;
            
            // Don't re-pool if not tracked as active
            if (!activeBuffers.Remove(buffer))
            {
                Debug.LogWarning("Attempted to release a buffer that wasn't retrieved from the pool.");
                return;
            }
            
            if (!activeBufferKeys.TryGetValue(buffer, out BufferKey key))
            {
                Debug.LogWarning("ComputeBufferManager.ReleaseBuffer called with an unknown buffer. Releasing immediately.");
                buffer.Release();
                return;
            }

            activeBufferKeys.Remove(buffer);

            if (bufferPools.TryGetValue(key, out Queue<ComputeBuffer> pool))
            {
                pool.Enqueue(buffer);
                PooledBufferCount++;
            }
            else
            {
                // Create a new pool for this type
                pool = new Queue<ComputeBuffer>();
                pool.Enqueue(buffer);
                bufferPools[key] = pool;
                PooledBufferCount++;
            }
        }

        /// <summary>
        /// Releases all buffers in the pool and active buffers.
        /// Should be called when cleaning up or during scene transitions.
        /// </summary>
        public void ReleaseAllBuffers()
        {
            // Release all active buffers
            foreach (var buffer in activeBuffers)
            {
                if (buffer != null)
                    buffer.Release();
            }
            activeBuffers.Clear();
            activeBufferKeys.Clear();
            
            // Release all pooled buffers
            foreach (var pool in bufferPools.Values)
            {
                while (pool.Count > 0)
                {
                    ComputeBuffer buffer = pool.Dequeue();
                    if (buffer != null)
                        buffer.Release();
                }
            }

            bufferPools.Clear();
            ReleaseStructuredBuffers();
            TotalBuffersCreated = 0;
            PooledBufferCount = 0;
        }
        #endregion

        #region Structured Buffer Methods
        public ComputeBuffer GetStructuredBuffer(Vector3Int resolution, int requiredCount, int stride, StructuredBufferRole role)
        {
            if (requiredCount <= 0)
            {
                throw new ArgumentOutOfRangeException(nameof(requiredCount), "Structured buffers must have at least one element.");
            }

            if (stride <= 0)
            {
                throw new ArgumentOutOfRangeException(nameof(stride), "Structured buffer stride must be positive.");
            }

            PersistentBufferKey key = new PersistentBufferKey(resolution, role);

            if (persistentStructuredBuffers.TryGetValue(key, out PersistentBufferInfo info))
            {
                bool needsResize = info.Buffer == null || info.Capacity < requiredCount || info.Stride != stride;

                if (needsResize)
                {
                    info.Buffer?.Release();
                    info.Buffer = new ComputeBuffer(requiredCount, stride, ComputeBufferType.Structured);
                    info.Capacity = requiredCount;
                    info.Stride = stride;
                    TotalBuffersCreated++;
                }

                return info.Buffer;
            }

            ComputeBuffer buffer = new ComputeBuffer(requiredCount, stride, ComputeBufferType.Structured);
            persistentStructuredBuffers[key] = new PersistentBufferInfo(buffer, requiredCount, stride);
            TotalBuffersCreated++;
            return buffer;
        }

        public ComputeBuffer GetStructuredVertexBuffer(Vector3Int resolution, int requiredCount)
        {
            return GetStructuredBuffer(resolution, requiredCount, 3 * sizeof(float), StructuredBufferRole.Vertex);
        }

        public ComputeBuffer GetStructuredNormalBuffer(Vector3Int resolution, int requiredCount)
        {
            return GetStructuredBuffer(resolution, requiredCount, 3 * sizeof(float), StructuredBufferRole.Normal);
        }

        public ComputeBuffer GetStructuredCounterBuffer(Vector3Int resolution)
        {
            return GetStructuredBuffer(resolution, 1, sizeof(uint), StructuredBufferRole.Counter);
        }

        public void ResetCounterBuffer(ComputeBuffer counterBuffer)
        {
            if (counterBuffer == null)
            {
                return;
            }

            counterBuffer.SetData(CounterResetData, 0, 0, 1);
        }

        public void ReleaseStructuredBuffers()
        {
            foreach (var entry in persistentStructuredBuffers.Values)
            {
                entry.Buffer?.Release();
            }

            persistentStructuredBuffers.Clear();
        }
        #endregion

        #region Unity Lifecycle Methods
        private void OnDestroy()
        {
            ReleaseAllBuffers();
        }
        
        // Log stats in editor
        private void OnGUI()
        {
            if (Debug.isDebugBuild)
            {
                GUI.Label(new Rect(10, 10, 300, 20), 
                    $"ComputeBuffers: {TotalBuffersCreated} total, {ActiveBufferCount} active, {PooledBufferCount} pooled");
            }
        }
        #endregion
    }
}