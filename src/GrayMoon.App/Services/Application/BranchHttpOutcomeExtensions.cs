namespace GrayMoon.App.Services.Application;

public static class BranchHttpOutcomeExtensions
{
    public static IResult ToHttpResult(this BranchHttpOutcome outcome)
    {
        if (outcome.ProblemTitle != null)
            return Results.Problem(outcome.ProblemTitle, statusCode: outcome.StatusCode);

        return outcome.StatusCode switch
        {
            400 => Results.BadRequest(outcome.Body),
            404 => Results.NotFound(outcome.Body),
            _ => Results.Ok(outcome.Body)
        };
    }
}
