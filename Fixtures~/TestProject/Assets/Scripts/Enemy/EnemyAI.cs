using UnityEngine;
using UnityEngine.AI;
using TestProject.Player;

namespace TestProject.Enemy
{
    public class EnemyAI : MonoBehaviour
    {
        [SerializeField] private float detectionRange = 10f;
        [SerializeField] private float attackRange = 2f;
        [SerializeField] private int damage = 10;

        private NavMeshAgent _agent;
        private Transform _target;

        void Awake() { _agent = GetComponent<NavMeshAgent>(); }

        void Update()
        {
            if (_target == null) return;
            var dist = Vector3.Distance(transform.position, _target.position);
            if (dist <= attackRange) _target.GetComponent<PlayerHealth>()?.TakeDamage(damage);
            else if (dist <= detectionRange) _agent.SetDestination(_target.position);
        }

        public void SetTarget(Transform target) => _target = target;
    }
}
