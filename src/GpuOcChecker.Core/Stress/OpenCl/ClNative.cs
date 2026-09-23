using System.Runtime.InteropServices;
using GpuOcChecker.Core.Telemetry;

namespace GpuOcChecker.Core.Stress.OpenCl;

/// <summary>The OpenCL 1.2 subset we need. OpenCL.dll is installed by AMD, NVIDIA and Intel drivers.</summary>
internal static class ClNative
{
    public const int CL_SUCCESS = 0;
    public const ulong CL_DEVICE_TYPE_GPU = 1 << 2;
    public const ulong CL_DEVICE_TYPE_ALL = 0xFFFFFFFF;
    public const uint CL_PLATFORM_NAME = 0x0902;
    public const uint CL_PLATFORM_VENDOR = 0x0903;
    public const uint CL_DEVICE_TYPE = 0x1000;
    public const uint CL_DEVICE_MAX_COMPUTE_UNITS = 0x1002;
    public const uint CL_DEVICE_MAX_WORK_GROUP_SIZE = 0x1004;
    public const uint CL_DEVICE_MAX_MEM_ALLOC_SIZE = 0x1010;
    public const uint CL_DEVICE_GLOBAL_MEM_SIZE = 0x101F;
    public const uint CL_DEVICE_NAME = 0x102B;
    public const uint CL_DEVICE_VENDOR = 0x102C;
    public const uint CL_DRIVER_VERSION = 0x102D;
    public const uint CL_DEVICE_BOARD_NAME_AMD = 0x4038;
    public const uint CL_PROGRAM_BUILD_LOG = 0x1183;
    public const ulong CL_MEM_READ_WRITE = 1;

    [DllImport(NativeLibraries.OpenCl)] public static extern int clGetPlatformIDs(uint num, [Out] IntPtr[]? platforms, out uint numPlatforms);
    [DllImport(NativeLibraries.OpenCl)] public static extern int clGetPlatformInfo(IntPtr platform, uint param, UIntPtr size, [Out] byte[]? value, out UIntPtr sizeRet);
    [DllImport(NativeLibraries.OpenCl)] public static extern int clGetDeviceIDs(IntPtr platform, ulong type, uint num, [Out] IntPtr[]? devices, out uint numDevices);
    [DllImport(NativeLibraries.OpenCl)] public static extern int clGetDeviceInfo(IntPtr device, uint param, UIntPtr size, [Out] byte[]? value, out UIntPtr sizeRet);
    [DllImport(NativeLibraries.OpenCl)] public static extern IntPtr clCreateContext(IntPtr props, uint numDevices, IntPtr[] devices, IntPtr callback, IntPtr userData, out int err);
    [DllImport(NativeLibraries.OpenCl)] public static extern IntPtr clCreateCommandQueue(IntPtr context, IntPtr device, ulong props, out int err);
    [DllImport(NativeLibraries.OpenCl)] public static extern IntPtr clCreateProgramWithSource(IntPtr context, uint count, string[] sources, IntPtr lengths, out int err);
    [DllImport(NativeLibraries.OpenCl)] public static extern int clBuildProgram(IntPtr program, uint numDevices, IntPtr[] devices, string? options, IntPtr callback, IntPtr userData);
    [DllImport(NativeLibraries.OpenCl)] public static extern int clGetProgramBuildInfo(IntPtr program, IntPtr device, uint param, UIntPtr size, [Out] byte[]? value, out UIntPtr sizeRet);
    [DllImport(NativeLibraries.OpenCl)] public static extern IntPtr clCreateKernel(IntPtr program, string name, out int err);
    [DllImport(NativeLibraries.OpenCl)] public static extern int clSetKernelArg(IntPtr kernel, uint index, UIntPtr size, ref IntPtr value);
    [DllImport(NativeLibraries.OpenCl)] public static extern int clSetKernelArg(IntPtr kernel, uint index, UIntPtr size, ref uint value);
    [DllImport(NativeLibraries.OpenCl)] public static extern int clSetKernelArg(IntPtr kernel, uint index, UIntPtr size, ref ulong value);
    [DllImport(NativeLibraries.OpenCl)] public static extern IntPtr clCreateBuffer(IntPtr context, ulong flags, UIntPtr size, IntPtr hostPtr, out int err);
    [DllImport(NativeLibraries.OpenCl)] public static extern int clEnqueueNDRangeKernel(IntPtr queue, IntPtr kernel, uint dims, UIntPtr[]? offset, UIntPtr[] global, UIntPtr[]? local, uint numEvents, IntPtr waitList, IntPtr evt);
    [DllImport(NativeLibraries.OpenCl)] public static extern int clEnqueueReadBuffer(IntPtr queue, IntPtr buffer, uint blocking, UIntPtr offset, UIntPtr size, IntPtr ptr, uint numEvents, IntPtr waitList, IntPtr evt);
    [DllImport(NativeLibraries.OpenCl)] public static extern int clEnqueueWriteBuffer(IntPtr queue, IntPtr buffer, uint blocking, UIntPtr offset, UIntPtr size, IntPtr ptr, uint numEvents, IntPtr waitList, IntPtr evt);
    [DllImport(NativeLibraries.OpenCl)] public static extern int clFinish(IntPtr queue);
    [DllImport(NativeLibraries.OpenCl)] public static extern int clReleaseMemObject(IntPtr mem);
    [DllImport(NativeLibraries.OpenCl)] public static extern int clReleaseKernel(IntPtr kernel);
    [DllImport(NativeLibraries.OpenCl)] public static extern int clReleaseProgram(IntPtr program);
    [DllImport(NativeLibraries.OpenCl)] public static extern int clReleaseCommandQueue(IntPtr queue);
    [DllImport(NativeLibraries.OpenCl)] public static extern int clReleaseContext(IntPtr context);

    public static string ErrorName(int code) => code switch
    {
        -1 => "CL_DEVICE_NOT_FOUND",
        -2 => "CL_DEVICE_NOT_AVAILABLE",
        -4 => "CL_MEM_OBJECT_ALLOCATION_FAILURE",
        -5 => "CL_OUT_OF_RESOURCES",
        -6 => "CL_OUT_OF_HOST_MEMORY",
        -11 => "CL_BUILD_PROGRAM_FAILURE",
        -14 => "CL_EXEC_STATUS_ERROR_FOR_EVENTS_IN_WAIT_LIST",
        -36 => "CL_INVALID_COMMAND_QUEUE",
        -54 => "CL_INVALID_WORK_GROUP_SIZE",
        -61 => "CL_INVALID_BUFFER_SIZE",
        -9999 => "NVIDIA_ILLEGAL_ACCESS/XID",
        _ => $"CL error {code}",
    };
}

/// <summary>Thrown when OpenCL reports an error; <see cref="DeviceLost"/> means the GPU/driver stopped responding.</summary>
public sealed class ClException : Exception
{
    public ClException(string what, int code)
        : base($"{what} failed: {ClNative.ErrorName(code)}")
    {
        Code = code;
    }

    public int Code { get; }

    /// <summary>Errors that on a running queue indicate a driver reset / TDR / device fault.</summary>
    public bool DeviceLost => Code is -5 or -14 or -36 or -9999 or -2;
}
