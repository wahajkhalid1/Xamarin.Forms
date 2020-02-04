using System;
using System.Collections.Generic;
using System.Reflection;
using Android.Content;
using Android.Views;
using Android.Util;
using Android.App;
using FLabelRenderer = Xamarin.Forms.Platform.Android.FastRenderers.LabelRenderer;
using ABuildVersionCodes = Android.OS.BuildVersionCodes;
using ABuild = Android.OS.Build;
using AView = Android.Views.View;
using ARelativeLayout = Android.Widget.RelativeLayout;
//using AToolbar = Android.Support.V7.Widget.Toolbar;
using System.Threading;
using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using Xamarin.Forms.Internals;
using System.Diagnostics;
using Xamarin.Forms.Core;

namespace Xamarin.Forms.Platform.Android
{
	public sealed class AndroidRendererActivator : RendererActivator
	{
		protected override object Activate(Type type, params object[] arguments)
		{
			object result = null;

			if (type == typeof(FLabelRenderer))
				result = new FLabelRenderer((Context)arguments[0]);

			if (type == typeof(PageRenderer))
				result = new PageRenderer((Context)arguments[0]);

			if (type == typeof(ListViewRenderer))
				result = new ListViewRenderer((Context)arguments[0]);

			var hitOrMiss = result == null ? "MISS" : "HIT";

			if (result == null)
				result = base.Activate(type, arguments);

			Log("ACTIVATOR {0}: {1}", hitOrMiss, type.Name);
			return result;
		}
	}
}