using System.Collections.Immutable;

namespace Scheduler.Application;

/// <summary>
/// Validates a complete signed SAT assignment against the application's own CNF.
/// Every solver adapter must pass this check before its model is materialized.
/// </summary>
public static class PersonalSelectionCnfModelValidator
{
    public static void Validate(PersonalSelectionCnf cnf, ImmutableArray<int> model)
    {
        ArgumentNullException.ThrowIfNull(cnf);
        if (cnf.VariableCount != cnf.Variables.Length ||
            !cnf.Variables.Select(variable => variable.VariableId)
                .SequenceEqual(Enumerable.Range(1, cnf.VariableCount)))
        {
            throw new InvalidOperationException("CNF variables must be sequential and complete.");
        }

        if (model.Length != cnf.VariableCount)
        {
            throw new InvalidOperationException("SAT model is incomplete.");
        }

        var assignments = new int[cnf.VariableCount + 1];
        for (var index = 0; index < model.Length; index++)
        {
            var literal = model[index];
            if (literal is 0 or int.MinValue)
            {
                throw new InvalidOperationException("SAT model contains an invalid literal.");
            }

            var variable = Math.Abs(literal);
            if (variable != index + 1 || assignments[variable] != 0)
            {
                throw new InvalidOperationException("SAT model must contain exactly one literal per ordered variable.");
            }

            assignments[variable] = literal;
        }

        foreach (var clause in cnf.Clauses)
        {
            if (!clause.Literals.Any(literal =>
                    literal is not 0 and not int.MinValue &&
                    Math.Abs(literal) <= cnf.VariableCount &&
                    assignments[Math.Abs(literal)] == literal))
            {
                throw new InvalidOperationException("SAT model does not satisfy the personal-selection CNF.");
            }
        }
    }
}
