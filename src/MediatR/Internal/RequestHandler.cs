using System.Collections.ObjectModel;

namespace MediatR.Internal
{
    using System;
    using System.Linq;
    using System.Threading;
    using System.Threading.Tasks;

    internal abstract class RequestHandlerBase
    {
        protected static object GetHandler(Type requestType, SingleInstanceFactory singleInstanceFactory, ref Collection<Exception> resolveExceptions)
        {
            try
            {
                return singleInstanceFactory(requestType);
            }
            catch (Exception e)
            {
                resolveExceptions?.Add(e);
                return null;
            }
        }

        protected static THandler GetHandler<THandler>(SingleInstanceFactory factory, ref Collection<Exception> resolveExceptions)
        {
            return (THandler)GetHandler(typeof(THandler), factory, ref resolveExceptions);
        }

        protected static THandler GetHandler<THandler>(SingleInstanceFactory factory)
        {
            Collection<Exception> swallowedExceptions = null;
            return (THandler)GetHandler(typeof(THandler), factory, ref swallowedExceptions);
        }

        protected static InvalidOperationException BuildException(object message, Collection<Exception> resolveExceptions)
        {
            Exception innerException = null;
            if (resolveExceptions.Count == 1)
            {
                innerException = resolveExceptions.First();
            }
            else if (resolveExceptions.Count > 1)
            {
                innerException = new AggregateException("Errors were encountered while resolving handlers", resolveExceptions);
            }

            return new InvalidOperationException("Handler was not found for request of type " + message.GetType() + ".\r\nContainer or service locator not configured properly or handlers not registered with your container.", innerException);
        }
    }

    internal abstract class RequestHandler : RequestHandlerBase
    {
        public abstract Task Handle(IRequest request, CancellationToken cancellationToken, IMediatorContext context,
            SingleInstanceFactory singleFactory, MultiInstanceFactory multiFactory);
    }

    internal abstract class RequestHandler<TResponse> : RequestHandlerBase
    {
        public abstract Task<TResponse> Handle(IRequest<TResponse> request, CancellationToken cancellationToken, IMediatorContext context,
            SingleInstanceFactory singleFactory, MultiInstanceFactory multiFactory);
    }

    internal class RequestHandlerImpl<TRequest, TResponse> : RequestHandler<TResponse>
        where TRequest : IRequest<TResponse>
    {
        private Func<TRequest, CancellationToken, IMediatorContext, SingleInstanceFactory, RequestHandlerDelegate<TResponse>> _handlerFactory;
        private object _syncLock = new object();
        private bool _initialized = false;

        public override Task<TResponse> Handle(IRequest<TResponse> request, CancellationToken cancellationToken, IMediatorContext context,
            SingleInstanceFactory singleFactory, MultiInstanceFactory multiFactory)
        {
            var handler = GetHandler((TRequest)request, cancellationToken, context, singleFactory);

            var pipeline = GetPipeline((TRequest)request, context, handler, multiFactory);

            return pipeline;
        }

        private RequestHandlerDelegate<TResponse> GetHandler(TRequest request, CancellationToken cancellationToken, IMediatorContext context, SingleInstanceFactory factory)
        {
            var resolveExceptions = new Collection<Exception>();
            LazyInitializer.EnsureInitialized(ref _handlerFactory, ref _initialized, ref _syncLock,
                () => GetHandlerFactory(factory, ref resolveExceptions));

            if (!_initialized || _handlerFactory == null)
            {
                throw BuildException(request, resolveExceptions);
            }

            return _handlerFactory(request, cancellationToken, context, factory);
        }

        private static Func<TRequest, CancellationToken, IMediatorContext, SingleInstanceFactory, RequestHandlerDelegate<TResponse>>
            GetHandlerFactory(SingleInstanceFactory factory, ref Collection<Exception> resolveExceptions)
        {
            if (GetHandler<IRequestHandler<TRequest, TResponse>>(factory, ref resolveExceptions) != null)
            {
                return (request, token, context, fac) => () =>
                  {
                      var handler = GetHandler<IRequestHandler<TRequest, TResponse>>(fac);
                      return Task.FromResult(handler.Handle(request));
                  };
            }

            if (GetHandler<IAsyncRequestHandler<TRequest, TResponse>>(factory, ref resolveExceptions) != null)
            {
                return (request, token, context, fac) =>
                {
                    var handler = GetHandler<IAsyncRequestHandler<TRequest, TResponse>>(fac);
                    return () => handler.Handle(request);
                };
            }

            if (GetHandler<ICancellableAsyncRequestHandler<TRequest, TResponse>>(factory, ref resolveExceptions) != null)
            {
                return (request, token, context, fac) =>
                {
                    var handler = GetHandler<ICancellableAsyncRequestHandler<TRequest, TResponse>>(fac);
                    return () => handler.Handle(request, token);
                };
            }

            if (GetHandler<IContextualAsyncRequestHandler<TRequest, TResponse>>(factory, ref resolveExceptions) != null)
            {
                return (request, token, context, fac) =>
                {
                    var handler = GetHandler<IContextualAsyncRequestHandler<TRequest, TResponse>>(fac);
                    return () => handler.Handle(request, context);
                };
            }

            return null;
        }

        private static Task<TResponse> GetPipeline(TRequest request, IMediatorContext context, RequestHandlerDelegate<TResponse> invokeHandler, MultiInstanceFactory factory)
        {
            var behaviors = factory(typeof(IPipelineBehavior<TRequest, TResponse>))
                .Cast<IPipelineBehavior<TRequest, TResponse>>()
                .Reverse();

            var aggregate = behaviors.Aggregate(invokeHandler, (next, pipeline) => () => pipeline.Handle(request, next, context));

            return aggregate();
        }
    }

    internal class RequestHandlerImpl<TRequest> : RequestHandler
        where TRequest : IRequest
    {
        private Func<TRequest, CancellationToken, IMediatorContext, SingleInstanceFactory, RequestHandlerDelegate<Unit>> _handlerFactory;
        private object _syncLock = new object();
        private bool _initialized = false;

        public override Task Handle(IRequest request, CancellationToken cancellationToken, IMediatorContext context,
            SingleInstanceFactory singleFactory, MultiInstanceFactory multiFactory)
        {
            var handler = GetHandler((TRequest)request, cancellationToken, context, singleFactory);

            var pipeline = GetPipeline((TRequest)request, context, handler, multiFactory);

            return pipeline;
        }

        private RequestHandlerDelegate<Unit> GetHandler(TRequest request, CancellationToken cancellationToken, IMediatorContext context, SingleInstanceFactory singleInstanceFactory)
        {
            var resolveExceptions = new Collection<Exception>();
            LazyInitializer.EnsureInitialized(ref _handlerFactory, ref _initialized, ref _syncLock,
                () => GetHandlerFactory(singleInstanceFactory, ref resolveExceptions));

            if (!_initialized || _handlerFactory == null)
            {
                throw BuildException(request, resolveExceptions);
            }

            return _handlerFactory(request, cancellationToken, context, singleInstanceFactory);
        }

        private static Func<TRequest, CancellationToken, IMediatorContext, SingleInstanceFactory, RequestHandlerDelegate<Unit>>
            GetHandlerFactory(SingleInstanceFactory factory, ref Collection<Exception> resolveExceptions)
        {
            if (GetHandler<IRequestHandler<TRequest>>(factory, ref resolveExceptions) != null)
            {
                return HandlerFactory;
            }
            if (GetHandler<IAsyncRequestHandler<TRequest>>(factory, ref resolveExceptions) != null)
            {
                return (request, token, context, fac) => async () =>
                {
                    var handler = GetHandler<IAsyncRequestHandler<TRequest>>(fac);
                    await handler.Handle(request).ConfigureAwait(false);
                    return Unit.Value;
                };
            }
            if (GetHandler<ICancellableAsyncRequestHandler<TRequest>>(factory, ref resolveExceptions) != null)
            {
                return (request, token, context, fac) => async () =>
                {
                    var handler = GetHandler<ICancellableAsyncRequestHandler<TRequest>>(fac);
                    await handler.Handle(request, token).ConfigureAwait(false);
                    return Unit.Value;
                };
            }
            if (GetHandler<IContextualAsyncRequestHandler<TRequest>>(factory, ref resolveExceptions) != null)
            {
                return (request, token, context, fac) => async () =>
                {
                    var handler = GetHandler<IContextualAsyncRequestHandler<TRequest>>(fac);
                    await handler.Handle(request, context).ConfigureAwait(false);
                    return Unit.Value;
                };
            }

            return null;
        }

        private static RequestHandlerDelegate<Unit> HandlerFactory(TRequest request, CancellationToken token, IMediatorContext context, SingleInstanceFactory fac)
        {
            return () =>
            {
                var handler = GetHandler<IRequestHandler<TRequest>>(fac);
                handler.Handle(request);
                return Task.FromResult(Unit.Value);
            };
        }

        private static Task<Unit> GetPipeline(TRequest request, IMediatorContext context, RequestHandlerDelegate<Unit> invokeHandler, MultiInstanceFactory factory)
        {
            var behaviors = factory(typeof(IPipelineBehavior<TRequest, Unit>))
                .Cast<IPipelineBehavior<TRequest, Unit>>()
                .Reverse();

            var aggregate = behaviors.Aggregate(invokeHandler, (next, pipeline) => () => pipeline.Handle(request, next, context));

            return aggregate();
        }
    }
}
