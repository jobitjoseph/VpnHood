﻿using Android.Content;
using Android.Graphics;
using Android.Graphics.Drawables;
using Android.Net;
using Android.OS;
using VpnHood.Client.Device.Droid.Utils;

namespace VpnHood.Client.Device.Droid;

public class AndroidDevice : IDevice
{
    private static AndroidDevice? _current;
    private int _notificationId = 3500;
    private Notification? _notification;
    private TaskCompletionSource<bool> _grantPermissionTaskSource = new();
    private TaskCompletionSource<bool> _startServiceTaskSource = new();
    private IPacketCapture? _packetCapture;
    private Activity? _activity;
    private const int RequestVpnPermissionId = 20100;

    public event EventHandler? OnStartAsService;
    public bool IsExcludeAppsSupported => true;
    public bool IsIncludeAppsSupported => true;
    public bool IsLogToConsoleSupported => false;
    public static AndroidDevice Current => _current ?? throw new InvalidOperationException($"{nameof(AndroidDevice)} has not been initialized.");
    public string OsInfo => $"{Build.Manufacturer}: {Build.Model}, Android: {Build.VERSION.Release}";

    public AndroidDevice()
    {
        if (_current != null)
            throw new InvalidOperationException($"Only one {nameof(AndroidDevice)} can be created.");

        _current = this;
    }

    public void Prepare<T>(T activity) where T : Activity, IActivityEvent
    {
        Prepare(activity, activity);
    }

    public void Prepare(Activity activity, IActivityEvent activityEvent)
    {
        _activity = activity;
        activityEvent.OnDestroyEvent += Activity_OnDestroy;
        activityEvent.OnActivityResultEvent += Activity_OnActivityResult;
    }

    public void InitNotification(Notification notification, int notificationId)
    {
        _notification = notification;
        _notificationId = notificationId;
    }

    private static Notification GetDefaultNotification()
    {
        const string channelId = "1000";
        var context = Application.Context;
        var notificationManager = context.GetSystemService(Context.NotificationService) as NotificationManager
            ?? throw new Exception("Could not resolve NotificationManager.");

        Notification.Builder notificationBuilder;
        if (OperatingSystem.IsAndroidVersionAtLeast(26))
        {
            var channel = new NotificationChannel(channelId, "VPN", NotificationImportance.Low);
            channel.EnableVibration(false);
            channel.EnableLights(false);
            channel.SetShowBadge(false);
            channel.LockscreenVisibility = NotificationVisibility.Public;
            notificationManager.CreateNotificationChannel(channel);
            notificationBuilder = new Notification.Builder(context, channelId);
        }
        else
        {
            notificationBuilder = new Notification.Builder(context);
        }

        // get default icon
        var appInfo = Application.Context.ApplicationInfo ?? throw new Exception("Could not retrieve app info");
        if (context.Resources == null) throw new Exception("Could not retrieve context.Resources.");
        var iconId = appInfo.Icon;
        if (iconId == 0) iconId = context.Resources.GetIdentifier("@mipmap/notification", "drawable", context.PackageName);
        if (iconId == 0) iconId = context.Resources.GetIdentifier("@mipmap/ic_launcher", "drawable", context.PackageName);
        if (iconId == 0) iconId = context.Resources.GetIdentifier("@mipmap/appicon", "drawable", context.PackageName);
        if (iconId == 0) throw new Exception("Could not retrieve default icon.");

        return notificationBuilder
            .SetSmallIcon(iconId)
            .SetOngoing(true)
            .Build();
    }

    public DeviceAppInfo[] InstalledApps
    {
        get
        {
            var deviceAppInfos = new List<DeviceAppInfo>();
            var packageManager = Application.Context.PackageManager ?? throw new Exception("Could not acquire PackageManager!");
            var intent = new Intent(Intent.ActionMain);
            intent.AddCategory(Intent.CategoryLauncher);
            var resolveInfoList = packageManager.QueryIntentActivities(intent, 0);
            foreach (var resolveInfo in resolveInfoList)
            {
                if (resolveInfo.ActivityInfo == null)
                    continue;

                var appName = resolveInfo.LoadLabel(packageManager);
                var appId = resolveInfo.ActivityInfo.PackageName;
                var icon = resolveInfo.LoadIcon(packageManager);
                if (appName is "" or null || appId is "" or null || icon == null)
                    continue;

                var deviceAppInfo = new DeviceAppInfo
                {
                    AppId = appId,
                    AppName = appName,
                    IconPng = EncodeToBase64(icon, 100)
                };
                deviceAppInfos.Add(deviceAppInfo);
            }

            return deviceAppInfos.ToArray();
        }
    }

    public async Task<IPacketCapture> CreatePacketCapture()
    {
        // Grant for permission if OnRequestVpnPermission is registered otherwise let service throw the error
        using var prepareIntent = VpnService.Prepare(_activity ?? Application.Context);
        if (prepareIntent != null)
        {
            _grantPermissionTaskSource = new TaskCompletionSource<bool>();
            if (_activity != null)
                _activity.StartActivityForResult(prepareIntent, RequestVpnPermissionId);
            else
                throw new Exception("Please open the app and grant VPN permission to proceed.");

            await Task.WhenAny(_grantPermissionTaskSource.Task, Task.Delay(TimeSpan.FromMinutes(2)));
            if (!_grantPermissionTaskSource.Task.IsCompletedSuccessfully)
                throw new Exception("Could not grant VPN permission in the given time.");

            if (!_grantPermissionTaskSource.Task.Result)
                throw new Exception("VPN permission has been rejected.");
        }

        // start service
        var intent = new Intent(Application.Context, typeof(AndroidPacketCapture));
        intent.PutExtra("manual", true);
        if (OperatingSystem.IsAndroidVersionAtLeast(26))
        {
            Application.Context.StartForegroundService(intent.SetAction("connect"));
        }
        else
        {
            Application.Context.StartService(intent.SetAction("connect"));
        }

        // check is service started
        _startServiceTaskSource = new TaskCompletionSource<bool>();
        await Task.WhenAny(_startServiceTaskSource.Task, Task.Delay(10000));
        if (_packetCapture == null)
            throw new Exception("Could not start VpnService in the given time.");

        return _packetCapture;
    }

    internal void OnServiceStartCommand(AndroidPacketCapture packetCapture, Intent? intent)
    {
        _packetCapture = packetCapture;
        _startServiceTaskSource.TrySetResult(true);

        // set foreground
        var notification = _notification ?? GetDefaultNotification();
        packetCapture.StartForeground(_notificationId, notification);

        // fire AutoCreate for always on
        var manual = intent?.GetBooleanExtra("manual", false) ?? false;
        if (!manual)
            OnStartAsService?.Invoke(this, EventArgs.Empty);
    }

    private static string EncodeToBase64(Drawable drawable, int quality)
    {
        var bitmap = DrawableToBitmap(drawable);
        var stream = new MemoryStream();
        if (!bitmap.Compress(Bitmap.CompressFormat.Png!, quality, stream))
            throw new Exception("Could not compress bitmap to png.");
        return Convert.ToBase64String(stream.ToArray());
    }

    private static Bitmap DrawableToBitmap(Drawable drawable)
    {
        if (drawable is BitmapDrawable { Bitmap: not null } drawable1)
            return drawable1.Bitmap;

        //var bitmap = CreateBitmap(drawable.IntrinsicWidth, drawable.IntrinsicHeight, Config.Argb8888);
        var bitmap = Bitmap.CreateBitmap(32, 32, Bitmap.Config.Argb8888!);
        var canvas = new Canvas(bitmap);
        drawable.SetBounds(0, 0, canvas.Width, canvas.Height);
        drawable.Draw(canvas);

        return bitmap;
    }

    private void Activity_OnDestroy(object? sender, EventArgs e)
    {
        _activity = null;
        _grantPermissionTaskSource.TrySetResult(false);
    }

    private void Activity_OnActivityResult(object? sender, ActivityResultEventArgs e)
    {
        if (e.RequestCode == RequestVpnPermissionId)
            _grantPermissionTaskSource.TrySetResult(e.ResultCode == Result.Ok);
    }
}