using Concourse.Domain.Common;
using MediatR;

namespace Concourse.Application.Abstractions.Messaging;

public interface ICommand : IRequest<Result>;

public interface ICommand<TResponse> : IRequest<Result<TResponse>>;
