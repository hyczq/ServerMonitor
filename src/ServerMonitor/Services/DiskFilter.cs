using System;
using System.Collections.Generic;
using ServerMonitor.Models;

namespace ServerMonitor.Services
{
    /// <summary>
    /// 过滤掉内存盘、容器叠加层、伪文件系统等"不是真实磁盘"的挂载点，
    /// 避免它们污染磁盘水位统计。
    /// </summary>
    internal static class DiskFilter
    {
        private static readonly string[] ExcludedFileSystems =
        {
            "tmpfs", "devtmpfs", "devfs", "overlay", "overlayfs", "squashfs",
            "ramfs", "shm", "none", "udev", "proc", "sysfs", "cgroup", "cgroup2",
            "debugfs", "securityfs", "pstore", "autofs", "mqueue", "hugetlbfs",
            "tracefs", "bpf", "configfs", "fusectl", "nsfs", "binfmt_misc", "efivarfs"
        };

        private static readonly string[] ExcludedMountPrefixes =
        {
            "/dev", "/sys", "/proc", "/run", "/snap", "/var/lib/docker",
            "/var/lib/kubelet", "/var/lib/containers"
        };

        private static readonly HashSet<string> ExcludedFileSystemSet =
            new HashSet<string>(ExcludedFileSystems, StringComparer.OrdinalIgnoreCase);

        public static bool IsRealDisk(string fileSystem, string mountPoint)
        {
            if (string.IsNullOrWhiteSpace(mountPoint)) return false;

            string fs = (fileSystem ?? string.Empty).Trim();
            string mount = mountPoint.Trim();

            // 网络文件系统形如 "host:/path"，仍然保留（NAS 挂载也需要监控）
            if (fs.StartsWith("/dev/", StringComparison.Ordinal) ||
                fs.StartsWith("\\\\", StringComparison.Ordinal) ||
                fs.Contains(":/"))
            {
                return true;
            }

            if (ExcludedFileSystemSet.Contains(fs)) return false;

            foreach (string prefix in ExcludedMountPrefixes)
            {
                if (mount.Equals(prefix, StringComparison.Ordinal) ||
                    mount.StartsWith(prefix + "/", StringComparison.Ordinal))
                {
                    return false;
                }
            }

            // 剩下的情况：既不是已知伪文件系统，也不在排除路径下 —— 当作真实磁盘
            return true;
        }

        /// <summary>按用量百分比降序排列，让界面先展示最紧张的盘。</summary>
        public static List<DiskUsage> Sort(IEnumerable<DiskUsage> disks)
        {
            var list = new List<DiskUsage>(disks);
            list.Sort((a, b) => b.UsedPercent.CompareTo(a.UsedPercent));
            return list;
        }
    }
}
