using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace ReconArt.Email.Sender.Internal
{
    internal static class ObjectValidator
    {
        internal static void ValidateObjectOrThrow(object obj)
        {
            var validationResults = new List<ValidationResult>();
            var validationContext = new ValidationContext(obj, serviceProvider: null, items: null);

            // Validate data annotations
            Validator.TryValidateObject(obj, validationContext, validationResults, validateAllProperties: true);

            // Validate IValidatableObject if implemented
            if (obj is IValidatableObject validatable)
            {
                var additionalResults = validatable.Validate(validationContext);
                validationResults.AddRange(additionalResults);
            }

            if (validationResults.Count > 0)
            {
                throw new ValidationException(
                    $"Validation failed for {obj.GetType().Name}. Errors: " +
                    string.Join("; ", validationResults.Select(r => r.ErrorMessage)));
            }
        }
    }
}
