using UnityEngine;

namespace Xianxia.Unity.T2
{
    /// <summary>Persistent ownership identity for a detached landmark root.</summary>
    [DisallowMultipleComponent]
    public sealed class NavigationLandmarkRootOwner : MonoBehaviour
    {
        [SerializeField] private Transform owner;

        public Transform Owner => owner;

        internal void Configure(Transform value)
        {
            owner = value;
        }

        internal void Release()
        {
            owner = null;
        }
    }
}
