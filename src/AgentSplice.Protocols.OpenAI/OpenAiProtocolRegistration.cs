using AgentSplice.Application.Protocols;
using AgentSplice.Protocols.OpenAI.ChatCompletions;
using AgentSplice.Protocols.OpenAI.Errors;
using AgentSplice.Protocols.OpenAI.Models;
using Microsoft.Extensions.DependencyInjection;

namespace AgentSplice.Protocols.OpenAI;

/// <summary>Registers the OpenAI-compatible ingress protocol implementations.</summary>
public static class OpenAiProtocolRegistration
{
    /// <summary>Registers the writers the application resolves through its protocol ports.</summary>
    public static IServiceCollection AddOpenAiCompatibilityProtocol(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.AddSingleton<IModelListWriter, OpenAiModelListWriter>();
        services.AddSingleton<IErrorEnvelopeWriter, OpenAiErrorEnvelopeWriter>();
        services.AddSingleton<IChatCompletionRequestCodec, OpenAiChatCompletionRequestCodec>();
        services.AddSingleton<IChatCompletionResponseCodec, OpenAiChatCompletionResponseCodec>();

        return services;
    }
}
