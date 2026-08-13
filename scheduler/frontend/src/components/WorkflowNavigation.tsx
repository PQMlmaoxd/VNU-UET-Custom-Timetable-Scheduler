import { COPY } from "../copy";
import type { StepId } from "../types";

const STEPS: { id: StepId; label: string; title: string }[] = [
  { id: 1, label: "01", title: COPY.steps.upload },
  { id: 2, label: "02", title: COPY.steps.selection },
  { id: 3, label: "03", title: COPY.steps.results },
];

type WorkflowNavigationProps = {
  currentStep: StepId;
  maxStep: StepId;
  onStepChange: (step: StepId) => void;
};

export function WorkflowNavigation({ currentStep, maxStep, onStepChange }: WorkflowNavigationProps) {
  return (
    <nav className="stepper workflow-navigation" aria-label={COPY.steps.navigationLabel}>
      <ol className="workflow-steps">
        {STEPS.map((step) => {
          const isActive = step.id === currentStep;
          const isDone = step.id < currentStep && step.id <= maxStep;
          const isDisabled = step.id > maxStep;

          return (
            <li key={step.id}>
              <button
                className="step-button workflow-step"
                type="button"
                disabled={isDisabled}
                aria-current={isActive ? "step" : undefined}
                data-active={isActive || undefined}
                data-done={isDone || undefined}
                onClick={() => onStepChange(step.id)}
              >
                <span aria-hidden="true">{isDone ? "✓" : step.label}</span>
                <strong>{step.title}</strong>
              </button>
            </li>
          );
        })}
      </ol>
    </nav>
  );
}
