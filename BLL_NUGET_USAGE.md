# RB.VideoTranslator.BLL NuGet Package

This document describes how to use the `RB.VideoTranslator.BLL` NuGet package to run the video translation pipeline.

## Installation

The package is available locally at `./nupkg/RB.VideoTranslator.BLL.1.0.0.nupkg`.

To install:
```
dotnet add package RB.VideoTranslator.BLL --source ./nupkg
```

Or reference it directly in your `.csproj`:
```xml
<PackageReference Include="RB.VideoTranslator.BLL" Version="1.0.0" />
```

## Quick Start

The BLL exposes a public `IPipelineRunner` interface that is the main entry point for consumers:

```csharp
using RB.VideoTranslator.BLL;

// Create a pipeline runner instance
IPipelineRunner runner = new PipelineBootstrapper();

// Initialize with configuration
await runner.InitializeAsync(
	configFilePath: "appsettings.json",
	options: opts => {
		opts.WorkingFolderPath = "/path/to/work";
		opts.AzureSubscriptionKey = "your-key";
		opts.AzureEndpointUrl = "your-endpoint";
		opts.AzureOpenAiEndpoint = "your-openai-endpoint";
	});

// Run the pipeline
await runner.RunAsync();
```

## Configuration

The pipeline is configured via `PipelineOptions`:

- **WorkingFolderPath**: Root folder for video processing (input, processing, output subdirectories)
- **AzureSubscriptionKey**: Azure Cognitive Services subscription key
- **AzureEndpointUrl**: Azure Cognitive Services endpoint URL
- **AzureOpenAiEndpoint**: Azure OpenAI endpoint for GPT models
- **AzureOpenAiDeployment**: Azure OpenAI deployment name (default: gpt-4o-mini)
- **FfmpegPath**: Path to ffmpeg executable (default: "ffmpeg")
- **PythonPath**: Path to Python executable (default: "python")
- **DemucsPath**: Path/executable for running Demucs (default: "python")
- **VenvPath**: Path to Python virtual environment (optional)
- **TranslationTargetLanguages**: List of target languages for translation (default: ["Bulgarian"])
- **OutputFolderPath**: Custom output folder (optional, defaults to <WorkingFolderPath>/output)
- **UseFemaleVoice**: Use female TTS voice instead of male (default: false)

## How It Works

1. **Initialization Phase**:
   - Loads configuration from appsettings.json
   - Merges programmatic option overrides
   - Creates the database (if needed)
   - Sets up folder structure (input, processing, output)
   - Validates required configuration values

2. **Execution Phase**:
   - Discovers new video files in the input folder
   - Creates jobs for new files, moving them to the processing folder
   - Processes all pending jobs sequentially
   - Advances each job through pipeline steps until completion

3. **Pipeline Steps**:
   - Audio extraction and separation (Demucs)
   - Speech-to-text transcription (Azure Speech)
   - Text translation (Azure OpenAI/GPT)
   - Text-to-speech synthesis (Azure Speech)
   - Audio mixing and video remuxing
   - Output to configured output folder

## Public API

### IPipelineRunner Interface

```csharp
public interface IPipelineRunner
{
	Task InitializeAsync(string? configFilePath = null, Action<PipelineOptions>? options = null);
	Task RunAsync();
}
```

### PipelineOptions Class

Configuration class with all pipeline settings (see Configuration section above).

### ConfigurationMerger Class

Static helper for merging CLI-style options into PipelineOptions programmatically:

```csharp
ConfigurationMerger.MergeCliOptions(
	opts,
	workFolder: "/path/to/work",
	azureKey: "key",
	azureEndpoint: "endpoint",
	openAiEndpoint: "endpoint",
	openAiDeployment: "deployment",
	targetLanguages: "Bulgarian,English",
	useFemaleVoice: true);
```

## Dependencies

The BLL package depends on:
- RB.VideoTranslator.Data (for database access and ORM)
- Azure.AI.OpenAI (for GPT integration)
- Azure.Identity (for Azure authentication)
- Microsoft.CognitiveServices.Speech (for Azure Speech Services)
- Microsoft.EntityFrameworkCore (for data access)
- Microsoft.Extensions.* packages (for configuration, logging, DI)

## Error Handling

The pipeline includes comprehensive error handling:
- Configuration validation with detailed error messages
- Per-job error logging without stopping other jobs
- Database transaction safety
- File system operation error reporting

All errors are logged using Microsoft.Extensions.Logging.

## Example: Minimal Consumer Application

```csharp
using Microsoft.Extensions.DependencyInjection;
using RB.VideoTranslator.BLL;

var runner = new PipelineBootstrapper();

try 
{
	await runner.InitializeAsync(
		configFilePath: "appsettings.json",
		options: null); // Use config file settings only

	await runner.RunAsync();
	Console.WriteLine("Pipeline completed successfully");
}
catch (Exception ex)
{
	Console.Error.WriteLine($"Pipeline failed: {ex.Message}");
	Environment.Exit(1);
}
```

## appsettings.json Example

```json
{
  "RBVideoTranslator": {
	"WorkingFolderPath": "C:\\VideoTranslator",
	"FfmpegPath": "C:\\tools\\ffmpeg.exe",
	"PythonPath": "C:\\tools\\python\\python.exe",
	"DemucsPath": "C:\\tools\\python\\python.exe",
	"VenvPath": "C:\\VideoTranslator\\venv",
	"AzureSubscriptionKey": "your-subscription-key",
	"AzureEndpointUrl": "https://region.tts.speech.microsoft.com",
	"AzureOpenAiEndpoint": "https://your-instance.openai.azure.com/",
	"AzureOpenAiDeployment": "gpt-4o-mini",
	"TranslationTargetLanguages": ["Bulgarian", "English"],
	"OutputFolderPath": "C:\\VideoTranslator\\output",
	"UseFemaleVoice": false
  }
}
```

## Package Contents

The NuGet package includes:
- `RB.VideoTranslator.BLL.dll` - Main library assembly
- All dependent assemblies
- Python tools for audio processing (copied to output directory)
- Full source code and documentation via symbol packages

## Support

For issues, please refer to the GitHub repository: https://github.com/rosenbalchev/VideoTranslatiorService
