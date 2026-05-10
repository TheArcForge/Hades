using UnityEngine;

namespace TestProject.Player
{
    public class PlayerController : MonoBehaviour
    {
        [SerializeField] private float moveSpeed = 5f;
        [SerializeField] private PlayerHealth health;
        [SerializeField] private Transform cameraTarget;

        private Rigidbody _rigidbody;

        void Awake() { _rigidbody = GetComponent<Rigidbody>(); }

        void Update()
        {
            float h = Input.GetAxis("Horizontal");
            float v = Input.GetAxis("Vertical");
            Move(new Vector3(h, 0, v));
        }

        public void Move(Vector3 direction)
        {
            _rigidbody.velocity = direction.normalized * moveSpeed;
        }
    }
}
