using System.Text.Json;

namespace LexExport;

// The game's first-run extraction (AssetExtractor.gd): `extract <game root> <job.json>`, every
// manifest the job file lists in one ExtractQueue run, so levels, models, and NPC looks export
// side by side within one core budget. The job file:
//
//   { "jobs": [
//       { "manifest": "<characters.json>", "out": "<folder>", "only": ["<id>", ...] },
//       { "manifest": "<levels.json>", "out": "<folder>", "looks_out": "<folder>" },
//       { "manifest": "<level>.looks.json", "out": "<folder>", "only": [...] } ] }
//
// "only" (optional) limits a job to those entries. A levels job's "looks_out" also exports each
// written level's NPC looks there; a looks job covers looks out of date whose level isn't being
// exported. Each manifest's kind is the name of its list, as in ManifestCommand.
public static class ExtractCommand
{
    public static int Run(string jobPath)
    {
        List<ExtractQueue.Job> jobs = new List<ExtractQueue.Job>();
        using (JsonDocument document = JsonDocument.Parse(File.ReadAllText(jobPath)))
        {
            foreach (JsonElement job in document.RootElement.GetProperty("jobs").EnumerateArray())
            {
                HashSet<string> only = job.TryGetProperty("only", out JsonElement ids)
                    ? ids.EnumerateArray().Select(id => id.GetString()).ToHashSet(StringComparer.Ordinal)
                    : null;
                string looksOut = job.TryGetProperty("looks_out", out JsonElement looks) ? looks.GetString() : null;
                string outDir = job.GetProperty("out").GetString();
                Directory.CreateDirectory(outDir);
                if (looksOut != null)
                    Directory.CreateDirectory(looksOut);
                jobs.Add(new ExtractQueue.Job(job.GetProperty("manifest").GetString(), outDir, only, looksOut));
            }
        }
        return ExtractQueue.Run(jobs);
    }
}
