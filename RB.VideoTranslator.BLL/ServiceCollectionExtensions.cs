using System.ClientModel;
using Azure.AI.OpenAI;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using RB.VideoTranslator.BLL.Services;

namespace RB.VideoTranslator.BLL;

public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Registers all RB.VideoTranslator BLL services and configures
    /// <see cref="PipelineOptions"/> from the <c>RBVideoTranslator</c> config section
    /// (when <paramref name="configuration"/> is provided) with optional programmatic overrides.
    /// </summary>
    /// <param name="services">The DI container.</param>
    /// <param name="configuration">
    ///   The application configuration. If supplied, options are bound from the
    ///   <c>RBVideoTranslator</c> section. Pass <see langword="null"/> to skip file-based binding.
    /// </param>
    /// <param name="configure">
    ///   Optional delegate applied after file-based binding — use to apply CLI argument overrides.
    /// </param>
    public static IServiceCollection AddRBVideoTranslator(
        this IServiceCollection services,
        IConfiguration? configuration = null,
        Action<PipelineOptions>? configure = null)
    {
        var optBuilder = services.AddOptions<PipelineOptions>();
        if (configuration is not null)
            optBuilder.BindConfiguration(PipelineOptionsDefaults.SectionName);
        if (configure is not null)
            optBuilder.PostConfigure(configure);

        services.AddScoped<IJobService, JobService>();
        services.AddScoped<IProcessRunner, DefaultProcessRunner>();
        services.AddScoped<IFileSystem, PhysicalFileSystem>();
        services.AddScoped<IMediaSeparatorService, MediaSeparatorService>();
        services.AddScoped<ISrtExtractorService, SrtExtractorService>();
        services.AddScoped<ISrtTranslatorService, SrtTranslatorService>();
        services.AddScoped<ISrtToAzureTtsService, SrtToAzureTtsService>();
        services.AddScoped<IVoiceRemoverService, VoiceRemoverService>();
        services.AddScoped<IAudioMixerService, AudioMixerService>();
        services.AddScoped<IVideoMuxerService, VideoMuxerService>();
        services.AddScoped<IPipelineOrchestrator, PipelineOrchestrator>();

        services.AddSingleton<IAzureSpeechEngine, AzureSpeechEngine>();
        services.AddSingleton<IAzureChatEngine>(sp =>
        {
            var opts = sp.GetRequiredService<IOptions<PipelineOptions>>().Value;
            var client = new AzureOpenAIClient(
                new Uri(opts.AzureOpenAiEndpoint),
                new ApiKeyCredential(opts.AzureSubscriptionKey));
            return new AzureChatEngine(client.GetChatClient(opts.AzureOpenAiDeployment));
        });

        return services;
    }
}
