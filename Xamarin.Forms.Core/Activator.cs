using System;
using System.Collections.Generic;
using System.Text;

namespace Xamarin.Forms.Core
{
	public class RendererActivator
	{

		protected virtual object Activate(Type type, params object[] arguments)
		{
			return Activator.CreateInstance(type, arguments);
		}
	}
}
