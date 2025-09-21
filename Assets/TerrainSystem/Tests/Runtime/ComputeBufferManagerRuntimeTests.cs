// using System.Collections;
// using System.Reflection;
// using NUnit.Framework;
// using UnityEngine;
// using UnityEngine.TestTools;
//
// namespace TerrainSystem.Tests
// {
//     public class ComputeBufferManagerRuntimeTests
//     {
//         [UnityTest]
//         public IEnumerator AppendBufferIsReusedWithCorrectType()
//         {
//             var manager = ComputeBufferManager.Instance;
//             manager.ReleaseAllBuffers();
//
//             const int count = 16;
//             const int stride = sizeof(int);
//
//             var buffer = manager.GetBuffer(count, stride, ComputeBufferType.Append);
//             Assert.IsNotNull(buffer);
//             var totalBuffersCreated = manager.TotalBuffersCreated;
//             Assert.GreaterOrEqual(totalBuffersCreated, 1);
//
//             manager.ReleaseBuffer(buffer);
//
//             var reusedBuffer = manager.GetBuffer(count, stride, ComputeBufferType.Append);
//             Assert.AreSame(buffer, reusedBuffer, "The Append buffer should be reused from the pool.");
//             Assert.DoesNotThrow(() => reusedBuffer.SetCounterValue(0), "Reused buffer should keep its Append type.");
//             Assert.AreEqual(totalBuffersCreated, manager.TotalBuffersCreated,
//                 "No additional buffers should be created when reusing.");
//
//             manager.ReleaseBuffer(reusedBuffer);
//             manager.ReleaseAllBuffers();
//
//             UnityEngine.Object.DestroyImmediate(manager.gameObject);
//             var instanceField = typeof(ComputeBufferManager).GetField("_instance", BindingFlags.Static | BindingFlags.NonPublic);
//             instanceField?.SetValue(null, null);
//
//             yield return null;
//         }
//     }
// }
