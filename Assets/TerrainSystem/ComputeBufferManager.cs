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

        #region Fields
        // Dictionary to store pools of buffers by size and stride
        private readonly Dictionary<BufferKey, Queue<ComputeBuffer>> bufferPools = 
            new Dictionary<BufferKey, Queue<ComputeBuffer>>();
        
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
            
            // Find which pool this buffer belongs to
            BufferKey key = new BufferKey(buffer.count, buffer.stride);
            
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
            TotalBuffersCreated = 0;
            PooledBufferCount = 0;
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