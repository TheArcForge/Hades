using UnityEngine;

namespace TestProject.Player
{
    [RequireComponent(typeof(Animator))]
    public class PlayerAnimator : MonoBehaviour
    {
        private Animator _animator;
        private static readonly int Speed = Animator.StringToHash("Speed");
        private static readonly int IsGrounded = Animator.StringToHash("IsGrounded");

        void Awake() { _animator = GetComponent<Animator>(); }
        public void SetSpeed(float speed) { _animator.SetFloat(Speed, speed); }
        public void SetGrounded(bool grounded) { _animator.SetBool(IsGrounded, grounded); }
    }
}
