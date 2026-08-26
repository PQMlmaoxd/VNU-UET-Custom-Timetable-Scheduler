import { COPY } from "../copy";
import type { StepId } from "../types";

const STEPS: { id: StepId; label: string }[] = [
  { id: 1, label: COPY.steps.upload },
  { id: 2, label: COPY.steps.selection },
  { id: 3, label: COPY.steps.results },
];

type WorkflowNavigationProps = {
  currentStep: StepId;
  maxStep: StepId;
  onStepChange: (step: StepId) => void;
};

export function WorkflowNavigation({ currentStep, maxStep, onStepChange }: WorkflowNavigationProps) {
  return (
    <nav className="workflow-strip" aria-label={COPY.steps.navigationLabel}>
      {STEPS.map((step, index) => {
        const isActive = step.id === currentStep;
        const isDone = step.id < currentStep && step.id <= maxStep;
        const isDisabled = step.id > maxStep;

        return (
          <button
            key={step.id}
            className="workflow-step"
            type="button"
            disabled={isDisabled}
            aria-current={isActive ? "step" : undefined}
            data-active={isActive || undefined}
            data-done={isDone || undefined}
            onClick={() => onStepChange(step.id)}
          >
            <span className="sr-only">{`${index + 1}. `}</span>
            {step.label}
          </button>
        );
      })}
    </nav>
  );
}
