using MediatR;
using System;
using System.Collections.Generic;
using System.Text;

namespace HSJM.AI.MediatR
{
    public interface IMediatRRequest<out TResponse> : IRequest<TResponse>
    {
    }
}
