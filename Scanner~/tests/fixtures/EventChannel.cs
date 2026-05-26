using UnityEngine;
using UnityEngine.Events;

namespace TestProject.Systems
{
    [CreateAssetMenu(menuName = "TestProject/Event Channel")]
    public class EventChannel : ScriptableObject
    {
        private UnityAction _listeners;
        public void Raise() { _listeners?.Invoke(); }
        public void Register(UnityAction listener) => _listeners += listener;
        public void Unregister(UnityAction listener) => _listeners -= listener;
    }
}
