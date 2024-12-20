using Unity.Burst;
using Unity.Mathematics;
using UnityEngine;
using static Unity.Physics.Math;

namespace Unity.Physics
{
    // Solve data for a constraint that limits one degree of angular freedom
    [NoAlias]
    struct RotationMotorJacobian
    {
        // Limited axis in motion A space
        public float3 AxisInMotionA;
        public float Target;

        public float MinAngle;
        public float MaxAngle;

        // Index of the limited axis
        public int AxisIndex;

        // Relative orientation of the motions before solving
        public quaternion MotionBFromA;

        // Rotation to joint space from motion space
        public quaternion MotionAFromJoint;
        public quaternion MotionBFromJoint;

        // Maximum impulse that can be applied to the motor before it caps out (not a breaking impulse)
        public float MaxImpulseOfMotor;

        // Accumulated impulse applied over the number of solver iterations
        public float AccumulatedImpulse;

        // Error before solving
        public float InitialError;

        // Fraction of the position error to correct per step
        public float Tau;

        // Fraction of the velocity error to correct per step
        public float Damping;

        // Build the Jacobian
        public void Build(
            MTransform aFromConstraint, MTransform bFromConstraint,
            MotionData motionA, MotionData motionB,
            Constraint constraint, float tau, float damping)
        {
            AxisIndex = constraint.ConstrainedAxis1D;
            AxisInMotionA = math.normalize(aFromConstraint.Rotation[AxisIndex]);
            Target = constraint.Target[AxisIndex];
            MinAngle = constraint.Min;
            MaxAngle = constraint.Max;
            Tau = tau;
            Damping = damping;
            MaxImpulseOfMotor = math.abs(constraint.MaxImpulse.x); //using as magnitude, y&z components are unused
            AccumulatedImpulse = 0.0f;

            MotionAFromJoint = new quaternion(aFromConstraint.Rotation);
            MotionBFromJoint = new quaternion(bFromConstraint.Rotation);

            Update(motionA, motionB);
        }

        public void Update(in MotionData motionA, in MotionData motionB)
        {
            MotionBFromA = math.mul(math.inverse(motionB.WorldFromMotion.rot), motionA.WorldFromMotion.rot);

            // Calculate the current error
            InitialError = CalculateError(MotionBFromA, out float tmp);
        }

        // Solve the Jacobian
        public void Solve(ref JacobianHeader jacHeader, ref MotionVelocity velocityA, ref MotionVelocity velocityB,
            Solver.StepInput stepInput)
        {
            // Predict the relative orientation at the end of the step
            quaternion futureMotionBFromA = JacobianUtilities.IntegrateOrientationBFromA(MotionBFromA,
                velocityA.AngularVelocity, velocityB.AngularVelocity, stepInput.Timestep);

            // Calculate the effective mass
            float3 axisInMotionB = math.mul(futureMotionBFromA, -AxisInMotionA);
            float effectiveMass;
            {
                float invEffectiveMass = math.csum(AxisInMotionA * AxisInMotionA * velocityA.InverseInertia +
                    axisInMotionB * axisInMotionB * velocityB.InverseInertia);
                effectiveMass = math.select(1.0f / invEffectiveMass, 0.0f, invEffectiveMass == 0.0f);
            }

            // Calculate the error, adjust by tau and damping, and apply an impulse to correct it
            float futureError = CalculateError(futureMotionBFromA, out float currentAngle);
            float correction = JacobianUtilities.CalculateCorrection(futureError, InitialError, Tau, Damping);

            float impulse = math.mul(effectiveMass, -correction) * stepInput.InvTimestep;
            impulse = JacobianUtilities.CapImpulse(impulse, ref AccumulatedImpulse, MaxImpulseOfMotor);

            // check if we hit a limit
            var correctedAngle = currentAngle - correction;
            var limitError = JacobianUtilities.CalculateError(correctedAngle, MinAngle, MaxAngle);
            if (math.abs(limitError) > 0)
            {
                impulse += math.mul(effectiveMass, -limitError) * stepInput.InvTimestep;
            }

            velocityA.ApplyAngularImpulse(impulse * AxisInMotionA);
            velocityB.ApplyAngularImpulse(impulse * axisInMotionB);
        }

        // Helper function
        private float CalculateError(quaternion motionBFromA, out float currentAngle)
        {
            // Calculate the relative joint frame rotation
            quaternion jointBFromA = math.mul(math.mul(math.inverse(MotionBFromJoint), motionBFromA), MotionAFromJoint);

            // extract current axis and angle between the two joint frames
            ((Quaternion)jointBFromA).ToAngleAxis(out var angleDeg, out var axis);
            // filter out any "out of rotation axis" components between the joint frames and make sure we are accounting
            // for a potential axis flip in the to-angle-axis calculation.
            angleDeg *= axis[AxisIndex];

            currentAngle = math.radians(angleDeg);
            return currentAngle - Target;
        }
    }
}
