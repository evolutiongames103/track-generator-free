using UnityEngine;
using UnityEngine.InputSystem;

//.Carcontroller.cs

namespace EvolutionGames.RacingTrack
{
    [RequireComponent(typeof(Rigidbody))]
    public class CarController : MonoBehaviour
    {
        // ... (all the same [Header] fields as before, unchanged) ...
        // I'll keep the full code for completeness, but only the input method is new.

        [Header("Wheel Colliders (Physics)")]
        public WheelCollider wheelFrontLeftCollider;
        public WheelCollider wheelFrontRightCollider;
        public WheelCollider wheelRearLeftCollider;
        public WheelCollider wheelRearRightCollider;

        [Header("Wheel Visuals (Meshes)")]
        public Transform wheelFrontLeftVisual;
        public Transform wheelFrontRightVisual;
        public Transform wheelRearLeftVisual;
        public Transform wheelRearRightVisual;

        [Header("Engine")]
        public float motorForce = 1500f;
        public float brakeForce = 3000f;
        public float maxSpeed = 40f;

        [Header("Steering")]
        public float maxSteerAngle = 35f;
        public float steerSpeed = 10f;
        [Range(0.2f, 1f)]
        public float highSpeedSteerReduction = 0.6f;

        [Header("Stability")]
        public float antiRollForce = 5000f;
        public Vector3 centreOfMassOffset = new Vector3(0f, -0.4f, 0f);

        private WheelFrictionCurve _defaultForwardFriction;
        private WheelFrictionCurve _defaultSidewaysFriction;

        private Rigidbody _rb;
        private float _currentSteer;
        private float _speedKmh;

        // Cached input – now updated from BOTH Update and FixedUpdate
        private float _throttleInput;
        private float _steerInput;

        void Awake()
        {
            _rb = GetComponent<Rigidbody>();
            _rb.centerOfMass = centreOfMassOffset;
        }

        void Start()
        {
            ApplySafeWheelSettings();
            wheelFrontLeftCollider.steerAngle = 0;
            wheelFrontRightCollider.steerAngle = 0;
        }

        void Update()
        {
            // Read input once per frame (for UI, etc.)
            RefreshInputFromNewSystem();
        }

        void FixedUpdate()
        {
            // CRITICAL: Refresh input again right before physics step
            // This eliminates any lag because FixedUpdate runs at a different rate.
            RefreshInputFromNewSystem();

            _speedKmh = _rb.linearVelocity.magnitude * 3.6f;

            HandleMotorAndBrakes();
            HandleSteering();
            ApplyAntiRoll();
            UpdateVisualWheels();
        }

        // ─────────────────────────────────────────────────────────────────────
        // Input – reads from new Input System, always fresh
        // ─────────────────────────────────────────────────────────────────────
        void RefreshInputFromNewSystem()
        {
            _throttleInput = 0f;
            _steerInput = 0f;

            if (Keyboard.current == null) return;

            if (Keyboard.current.wKey.isPressed || Keyboard.current.upArrowKey.isPressed)
                _throttleInput = 1f;
            if (Keyboard.current.sKey.isPressed || Keyboard.current.downArrowKey.isPressed)
                _throttleInput = -1f;

            if (Keyboard.current.aKey.isPressed || Keyboard.current.leftArrowKey.isPressed)
                _steerInput = -1f;
            if (Keyboard.current.dKey.isPressed || Keyboard.current.rightArrowKey.isPressed)
                _steerInput = 1f;

            // (Optional) Debug: uncomment to see if input changes instantly
            // Debug.Log($"Throttle: {_throttleInput}, Steer: {_steerInput}");
        }

        // The rest of the methods (HandleMotorAndBrakes, HandleSteering, etc.)
        // are IDENTICAL to the last working version. I'll include them for completeness.
        void HandleMotorAndBrakes()
        {
            wheelFrontLeftCollider.motorTorque = 0;
            wheelFrontRightCollider.motorTorque = 0;
            wheelRearLeftCollider.motorTorque = 0;
            wheelRearRightCollider.motorTorque = 0;
            wheelFrontLeftCollider.brakeTorque = 0;
            wheelFrontRightCollider.brakeTorque = 0;
            wheelRearLeftCollider.brakeTorque = 0;
            wheelRearRightCollider.brakeTorque = 0;

            float speed = _rb.linearVelocity.magnitude;
            bool movingForward = Vector3.Dot(_rb.linearVelocity, transform.forward) > 0f;

            bool shouldBrake = false;
            if (_throttleInput < 0f && movingForward && speed > 0.5f)
                shouldBrake = true;
            if (_throttleInput > 0f && !movingForward && speed > 0.5f)
                shouldBrake = true;

            if (shouldBrake)
            {
                float brake = Mathf.Abs(_throttleInput) * brakeForce;
                wheelFrontLeftCollider.brakeTorque = brake;
                wheelFrontRightCollider.brakeTorque = brake;
                wheelRearLeftCollider.brakeTorque = brake;
                wheelRearRightCollider.brakeTorque = brake;
                return;
            }

            if (speed < maxSpeed)
            {
                float torque = _throttleInput * motorForce;
                wheelRearLeftCollider.motorTorque = torque;
                wheelRearRightCollider.motorTorque = torque;
            }
        }

        void HandleSteering()
        {
            float speedFactor = Mathf.Lerp(1f, highSpeedSteerReduction, _rb.linearVelocity.magnitude / maxSpeed);
            float targetSteer = _steerInput * maxSteerAngle * speedFactor;
            _currentSteer = Mathf.Lerp(_currentSteer, targetSteer, Time.fixedDeltaTime * steerSpeed);
            wheelFrontLeftCollider.steerAngle = _currentSteer;
            wheelFrontRightCollider.steerAngle = _currentSteer;
        }

        void ApplyAntiRoll()
        {
            ApplyAntiRollAtAxle(wheelFrontLeftCollider, wheelFrontRightCollider);
            ApplyAntiRollAtAxle(wheelRearLeftCollider, wheelRearRightCollider);
        }

        void ApplyAntiRollAtAxle(WheelCollider left, WheelCollider right)
        {
            WheelHit hit;
            float leftTravel = 1f;
            float rightTravel = 1f;
            bool leftGrounded = left.GetGroundHit(out hit);
            if (leftGrounded) leftTravel = (-left.transform.InverseTransformPoint(hit.point).y - left.radius) / left.suspensionDistance;
            bool rightGrounded = right.GetGroundHit(out hit);
            if (rightGrounded) rightTravel = (-right.transform.InverseTransformPoint(hit.point).y - right.radius) / right.suspensionDistance;

            float antiRollForceValue = (leftTravel - rightTravel) * antiRollForce;

            if (leftGrounded)
                _rb.AddForceAtPosition(left.transform.up * -antiRollForceValue, left.transform.position);
            if (rightGrounded)
                _rb.AddForceAtPosition(right.transform.up * antiRollForceValue, right.transform.position);
        }

        void UpdateVisualWheels()
        {
            SyncWheelVisual(wheelFrontLeftCollider, wheelFrontLeftVisual);
            SyncWheelVisual(wheelFrontRightCollider, wheelFrontRightVisual);
            SyncWheelVisual(wheelRearLeftCollider, wheelRearLeftVisual);
            SyncWheelVisual(wheelRearRightCollider, wheelRearRightVisual);
        }

        void SyncWheelVisual(WheelCollider collider, Transform visual)
        {
            if (collider == null || visual == null) return;
            Vector3 pos;
            Quaternion rot;
            collider.GetWorldPose(out pos, out rot);
            visual.position = pos;
            visual.rotation = rot;
        }

        void ApplySafeWheelSettings()
        {
            _defaultForwardFriction = new WheelFrictionCurve();
            _defaultForwardFriction.extremumSlip = 0.25f;
            _defaultForwardFriction.extremumValue = 1.0f;
            _defaultForwardFriction.asymptoteSlip = 0.5f;
            _defaultForwardFriction.asymptoteValue = 0.8f;
            _defaultForwardFriction.stiffness = 2.5f;

            _defaultSidewaysFriction = new WheelFrictionCurve();
            _defaultSidewaysFriction.extremumSlip = 0.2f;
            _defaultSidewaysFriction.extremumValue = 1.2f;
            _defaultSidewaysFriction.asymptoteSlip = 0.5f;
            _defaultSidewaysFriction.asymptoteValue = 0.9f;
            _defaultSidewaysFriction.stiffness = 2.0f;

            WheelCollider[] wheels = { wheelFrontLeftCollider, wheelFrontRightCollider, wheelRearLeftCollider, wheelRearRightCollider };
            foreach (var w in wheels)
            {
                if (w == null) continue;
                w.mass = 20f;
                JointSpring spring = w.suspensionSpring;
                spring.spring = 25000f;
                spring.damper = 4500f;
                spring.targetPosition = 0.5f;
                w.suspensionSpring = spring;
                w.suspensionDistance = 0.25f;
                w.forwardFriction = _defaultForwardFriction;
                w.sidewaysFriction = _defaultSidewaysFriction;
            }
        }

        public float SpeedKmh => _speedKmh;
        public float SpeedNormal => Mathf.Clamp01(_speedKmh / (maxSpeed * 3.6f));
    }
}