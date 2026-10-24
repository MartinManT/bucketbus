using Amazon.Runtime;
using Amazon.Runtime.Internal;
using Amazon.S3;
using Amazon.S3.Model;

namespace BucketBus.S3;

internal sealed class ExactKeyS3Client(AWSCredentials credentials, AmazonS3Config configuration) : AmazonS3Client(credentials, configuration)
{
    protected override void CustomizeRuntimePipeline(RuntimePipeline pipeline)
    {
        base.CustomizeRuntimePipeline(pipeline);
        // Ambient SDK client-side monitoring must not export exception messages or identities.
        if (pipeline.Handlers.Any(h => h is CSMCallAttemptHandler)) pipeline.RemoveHandler<CSMCallAttemptHandler>();
        if (pipeline.Handlers.Any(h => h is CSMCallEventHandler)) pipeline.RemoveHandler<CSMCallEventHandler>();
        pipeline.AddHandlerBefore<Signer>(new LeadingSlashHeadHandler());
    }

    private sealed class LeadingSlashHeadHandler : PipelineHandler
    {
        public override Task<T> InvokeAsync<T>(IExecutionContext executionContext)
        {
            var request = executionContext.RequestContext.Request;
            if (executionContext.RequestContext.OriginalRequest is GetObjectMetadataRequest { Key: { } key }
                && key.StartsWith('/') && request.Endpoint.AbsolutePath != "/")
            {
                // AWSSDK.Core 4.0.102.12 ComposeUrl omits the separator when a resolved
                // resource begins with '/'. For path-style HEAD, put the bucket path in
                // the resource template before signing. The SDK still encodes and signs
                // the unchanged key; the endpoint now ends in '/', preserving both slashes.
                // Idempotent across retries; only the proven affected request shape changes.
                request.ResourcePath = request.Endpoint.AbsolutePath.TrimEnd('/') + request.ResourcePath;
                request.Endpoint = new Uri(request.Endpoint.GetLeftPart(UriPartial.Authority) + "/");
            }
            return base.InvokeAsync<T>(executionContext);
        }
    }
}
