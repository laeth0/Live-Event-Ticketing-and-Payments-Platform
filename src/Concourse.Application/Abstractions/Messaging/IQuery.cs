using Concourse.Domain.Common;
using MediatR;

namespace Concourse.Application.Abstractions.Messaging;

public interface IQuery<TResponse> : IRequest<Result<TResponse>>;
