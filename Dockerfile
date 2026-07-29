# Planning scaffold. Update project paths after solution bootstrap.
FROM mcr.microsoft.com/dotnet/aspnet:8.0 AS runtime
WORKDIR /app
EXPOSE 5280

# The build stages will be added when src/AgentSplice.Api exists.
# This placeholder deliberately fails rather than publishing a misleading image.
CMD ["sh", "-c", "echo 'AgentSplice implementation has not been bootstrapped yet.' && exit 1"]
