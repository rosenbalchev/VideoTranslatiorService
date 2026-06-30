using System.CommandLine;
using System.CommandLine.Parsing;
using RB.VideoTranslator.BLL;

namespace RB.VideoTranslator.CLI;

internal sealed class Program
{
    private static async Task<int> Main(string[] args)
    {
        // All options are optional on the CLI — required values can come from appsettings.json.
        // Runtime validation happens inside the PipelineRunner after options are merged.

        var workFolderOpt = new Option<DirectoryInfo?>("--work-folder")
        {
            Description = "Root folder for all pipeline data. Overrides WorkingFolderPath in appsettings.json. " +
                          "Subfolders 'input', 'processing', 'output' are created automatically."
        };

        var ffmpegOpt = new Option<string?>("--ffmpeg")
        {
            Description = "Path to the ffmpeg executable. Overrides FfmpegPath in appsettings.json."
        };

        var pythonOpt = new Option<string?>("--python")
        {
            Description = "Path to the python executable. Overrides PythonPath in appsettings.json."
        };

        var demucsOpt = new Option<string?>("--demucs")
        {
            Description = "Python executable used to run 'python -m demucs'. Overrides DemucsPath in appsettings.json."
        };

        var azureKeyOpt = new Option<string?>("--azure-key")
        {
            Description = "Azure Cognitive Services subscription key. Overrides AzureSubscriptionKey in appsettings.json."
        };

        var azureEndpointOpt = new Option<string?>("--azure-endpoint")
        {
            Description = "Azure Cognitive Services endpoint URL. Overrides AzureEndpointUrl in appsettings.json."
        };

        var openAiEndpointOpt = new Option<string?>("--openai-endpoint")
        {
            Description = "Azure AI Services root endpoint URL for GPT. Overrides AzureOpenAiEndpoint in appsettings.json."
        };

        var openAiDeploymentOpt = new Option<string?>("--openai-deployment")
        {
            Description = "Azure OpenAI deployment name. Overrides AzureOpenAiDeployment in appsettings.json."
        };

        var targetLangOpt = new Option<string?>("--target-lang")
        {
            Description = "Comma-separated target languages, e.g. \"Bulgarian,English\". " +
                          "Overrides TranslationTargetLanguages in appsettings.json."
        };

        var venvOpt = new Option<DirectoryInfo?>("--venv")
        {
            Description = "Path to a Python virtual environment. Overrides VenvPath in appsettings.json."
        };

        var femaleOpt = new Option<bool>("--female")
        {
            Description = "Use a female Azure TTS voice instead of the default male voice."
        };

        var root = new RootCommand(
            "RB.VideoTranslator — picks up video files and advances them through the translation pipeline. " +
            "Configure via appsettings.json (RBVideoTranslator section); CLI arguments override config values.");

        root.Add(workFolderOpt);
        root.Add(ffmpegOpt);
        root.Add(pythonOpt);
        root.Add(demucsOpt);
        root.Add(azureKeyOpt);
        root.Add(azureEndpointOpt);
        root.Add(openAiEndpointOpt);
        root.Add(openAiDeploymentOpt);
        root.Add(targetLangOpt);
        root.Add(venvOpt);
        root.Add(femaleOpt);

        root.SetAction(async parseResult =>
        {
            try
            {
                // Create pipeline runner from BLL
                IPipelineRunner runner = new PipelineBootstrapper();

                // Initialize pipeline with merged CLI options
                await runner.InitializeAsync(
                    configFilePath: null, // uses default "appsettings.json"
                    options: opts => MergeCliOptions(parseResult, opts, workFolderOpt, ffmpegOpt, pythonOpt, 
                        demucsOpt, azureKeyOpt, azureEndpointOpt, openAiEndpointOpt, openAiDeploymentOpt, 
                        targetLangOpt, venvOpt, femaleOpt));

                // Run the pipeline
                await runner.RunAsync();
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"FATAL ERROR: {ex.Message}");
                if (ex.InnerException != null)
                    Console.Error.WriteLine($"Inner exception: {ex.InnerException.Message}");
                Environment.Exit(1);
            }
        });

        return await root.Parse(args).InvokeAsync();
    }

    /// <summary>
    /// Merges CLI-provided options from the command-line parse result into the pipeline options.
    /// </summary>
    private static void MergeCliOptions(
        ParseResult parseResult,
        PipelineOptions opts,
        Option<DirectoryInfo?> workFolderOpt,
        Option<string?> ffmpegOpt,
        Option<string?> pythonOpt,
        Option<string?> demucsOpt,
        Option<string?> azureKeyOpt,
        Option<string?> azureEndpointOpt,
        Option<string?> openAiEndpointOpt,
        Option<string?> openAiDeploymentOpt,
        Option<string?> targetLangOpt,
        Option<DirectoryInfo?> venvOpt,
        Option<bool> femaleOpt)
    {
        var workFolder = parseResult.GetValue(workFolderOpt);
        var venvFolder = parseResult.GetValue(venvOpt);
        var useFemale = parseResult.GetValue(femaleOpt);

        ConfigurationMerger.MergeCliOptions(
            opts,
            workFolder: workFolder?.FullName,
            ffmpegPath: parseResult.GetValue(ffmpegOpt),
            pythonPath: parseResult.GetValue(pythonOpt),
            demucsPath: parseResult.GetValue(demucsOpt),
            azureKey: parseResult.GetValue(azureKeyOpt),
            azureEndpoint: parseResult.GetValue(azureEndpointOpt),
            openAiEndpoint: parseResult.GetValue(openAiEndpointOpt),
            openAiDeployment: parseResult.GetValue(openAiDeploymentOpt),
            targetLanguages: parseResult.GetValue(targetLangOpt),
            venvPath: venvFolder?.FullName,
            useFemaleVoice: useFemale ? true : null);
    }
}
