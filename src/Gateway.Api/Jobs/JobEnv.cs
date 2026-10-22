using System.Globalization;
using System.Text;
using Gateway.Api.Data;

namespace Gateway.Api.Jobs;

/// <summary>
/// The environment a job container starts with (docs/jobs.md section 3): the job's
/// secret env plus four variables the gateway sets. The gateway variables win over a
/// secret key of the same name so a secret can never replace the script.
/// </summary>
public static class JobEnv
{
    public const string Name = "GATEWAY_JOB_NAME";
    public const string RunId = "GATEWAY_JOB_RUN_ID";
    public const string Trigger = "GATEWAY_JOB_TRIGGER";

    /// <summary>The script body, base64 of its UTF-8 bytes.</summary>
    public const string Script = "GATEWAY_JOB_SCRIPT";

    public static IReadOnlyDictionary<string, string> Build(
        IReadOnlyDictionary<string, string> secretEnv, Job job, JobRun run)
    {
        var env = new Dictionary<string, string>(secretEnv, StringComparer.Ordinal)
        {
            [Name] = job.Name,
            [RunId] = run.Id.ToString(CultureInfo.InvariantCulture),
            [Trigger] = run.Trigger,
            [Script] = Convert.ToBase64String(Encoding.UTF8.GetBytes(job.Script)),
        };
        return env;
    }
}
