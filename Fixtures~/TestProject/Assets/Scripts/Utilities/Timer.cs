using System;
using UnityEngine;

namespace TestProject.Utilities
{
    public class Timer : MonoBehaviour
    {
        private float _remaining;
        private Action _onComplete;
        private bool _running;

        public void StartTimer(float duration, Action onComplete)
        {
            _remaining = duration;
            _onComplete = onComplete;
            _running = true;
        }

        void Update()
        {
            if (!_running) return;
            _remaining -= Time.deltaTime;
            if (_remaining <= 0) { _running = false; _onComplete?.Invoke(); }
        }
    }
}
