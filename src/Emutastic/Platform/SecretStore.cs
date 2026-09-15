using System;
using System.Runtime.InteropServices;

namespace Emutastic.Platform
{
    /// <summary>
    /// Credentials in the desktop keyring: the freedesktop Secret Service (GNOME Keyring, KWallet,
    /// KeePassXC, …) reached through libsecret, which is where gh and git-credential-libsecret keep
    /// GitHub tokens on Linux. Upstream protects the same values with DPAPI; this is the Linux
    /// counterpart.
    ///
    /// Items carry two attributes: <c>credential</c> (what the secret is) and <c>profile</c> (the
    /// data root it belongs to), so a portable copy never reads or replaces the installed app's
    /// sign-in.
    ///
    /// Every call BLOCKS — libsecret runs a private main loop over D-Bus, and the service may put
    /// up an unlock prompt and wait for the user — so never call these on the UI thread. Calls are
    /// serialized. A missing libsecret, no Secret Service on the session bus, or a dismissed prompt
    /// all come back as false with a reason, never as an exception.
    /// </summary>
    internal static class SecretStore
    {
        private const string LibSecret = "libsecret-1.so.0";
        private const string LibGlib   = "libglib-2.0.so.0";

        private const string SchemaName     = "io.github.codingncaffeine.Emutastic";
        private const string AttrCredential = "credential";
        private const string AttrProfile    = "profile";

        // SecretSchema (libsecret/secret-schema.h) on LP64: name at 0, flags at 8, then
        // attributes[32] as 16-byte {name, type} pairs from offset 16 (a NULL name ends the
        // list), then the reserved fields — 592 bytes in all.
        private const int SchemaSize = 592;
        private const int AttributeBase = 16, AttributeSize = 16;
        private const int SECRET_SCHEMA_NONE = 0, SECRET_SCHEMA_ATTRIBUTE_STRING = 0;

        private static readonly object _gate = new();
        private static IntPtr _schema;               // built once, lives for the process
        private static IntPtr _strHash, _strEqual;   // g_str_hash / g_str_equal, for the attribute table
        private static string? _unavailable;         // why libsecret could not be loaded, once known

        [DllImport(LibSecret)]
        private static extern int secret_password_storev_sync(IntPtr schema, IntPtr attributes, IntPtr collection,
            IntPtr label, IntPtr password, IntPtr cancellable, ref IntPtr error);

        [DllImport(LibSecret)]
        private static extern IntPtr secret_password_lookupv_sync(IntPtr schema, IntPtr attributes,
            IntPtr cancellable, ref IntPtr error);

        [DllImport(LibSecret)]
        private static extern int secret_password_clearv_sync(IntPtr schema, IntPtr attributes,
            IntPtr cancellable, ref IntPtr error);

        [DllImport(LibSecret)]
        private static extern void secret_password_free(IntPtr password);   // wipes, then frees

        [DllImport(LibGlib)]
        private static extern IntPtr g_hash_table_new(IntPtr hashFunc, IntPtr keyEqualFunc);

        [DllImport(LibGlib)]
        private static extern int g_hash_table_insert(IntPtr table, IntPtr key, IntPtr value);

        [DllImport(LibGlib)]
        private static extern void g_hash_table_unref(IntPtr table);

        [DllImport(LibGlib)]
        private static extern void g_error_free(IntPtr error);

        /// <summary>Stores <paramref name="secret"/> in the default collection, replacing any
        /// item with the same attributes.</summary>
        public static bool TryStore(string credential, string secret, string label, out string? error)
        {
            lock (_gate)
            {
                if (!EnsureLoaded(out error)) return false;
                using var attributes = new AttributeTable(credential);
                IntPtr labelPtr = Marshal.StringToCoTaskMemUTF8(label);
                IntPtr secretPtr = Marshal.StringToCoTaskMemUTF8(secret);
                IntPtr gerror = IntPtr.Zero;
                try
                {
                    bool stored = secret_password_storev_sync(_schema, attributes.Handle, IntPtr.Zero,
                        labelPtr, secretPtr, IntPtr.Zero, ref gerror) != 0;
                    error = TakeError(gerror) ?? (stored ? null : "the keyring did not store the item");
                    return error == null;
                }
                finally
                {
                    Marshal.FreeCoTaskMem(labelPtr);
                    Marshal.ZeroFreeCoTaskMemUTF8(secretPtr);
                }
            }
        }

        /// <summary>
        /// Reads a stored secret. True with <paramref name="secret"/> null means the keyring
        /// answered and holds no such item; false means it could not be asked (see
        /// <paramref name="error"/>).
        /// </summary>
        public static bool TryLookup(string credential, out string? secret, out string? error)
        {
            secret = null;
            lock (_gate)
            {
                if (!EnsureLoaded(out error)) return false;
                using var attributes = new AttributeTable(credential);
                IntPtr gerror = IntPtr.Zero;
                IntPtr value = secret_password_lookupv_sync(_schema, attributes.Handle, IntPtr.Zero, ref gerror);
                try
                {
                    error = TakeError(gerror);
                    if (error != null) return false;
                    secret = value == IntPtr.Zero ? null : Marshal.PtrToStringUTF8(value);
                    return true;
                }
                finally
                {
                    if (value != IntPtr.Zero) secret_password_free(value);
                }
            }
        }

        /// <summary>Removes the item if there is one. True when nothing of it remains stored.</summary>
        public static bool TryClear(string credential, out string? error)
        {
            lock (_gate)
            {
                if (!EnsureLoaded(out error)) return false;
                using var attributes = new AttributeTable(credential);
                IntPtr gerror = IntPtr.Zero;
                // FALSE without an error only means nothing matched.
                secret_password_clearv_sync(_schema, attributes.Handle, IntPtr.Zero, ref gerror);
                error = TakeError(gerror);
                return error == null;
            }
        }

        // Caller holds _gate. Loads both libraries and builds the schema on first use; a library
        // that is missing stays missing for the process, but a keyring that is merely not running
        // is asked again on the next call.
        private static bool EnsureLoaded(out string? error)
        {
            error = _unavailable;
            if (_schema != IntPtr.Zero) return true;
            if (error != null) return false;
            try
            {
                if (IntPtr.Size != 8)
                    throw new PlatformNotSupportedException("the SecretSchema layout here assumes a 64-bit process");
                IntPtr glib = NativeLibrary.Load(LibGlib);
                _strHash  = NativeLibrary.GetExport(glib, "g_str_hash");
                _strEqual = NativeLibrary.GetExport(glib, "g_str_equal");
                IntPtr secret = NativeLibrary.Load(LibSecret);
                foreach (string export in new[] { "secret_password_storev_sync", "secret_password_lookupv_sync",
                                                  "secret_password_clearv_sync", "secret_password_free" })
                    NativeLibrary.GetExport(secret, export);
            }
            catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException or PlatformNotSupportedException)
            {
                error = _unavailable = $"libsecret unavailable: {ex.Message}";
                return false;
            }

            IntPtr schema = Marshal.AllocHGlobal(SchemaSize);
            Marshal.Copy(new byte[SchemaSize], 0, schema, SchemaSize);
            Marshal.WriteIntPtr(schema, 0, Marshal.StringToCoTaskMemUTF8(SchemaName));
            Marshal.WriteInt32(schema, 8, SECRET_SCHEMA_NONE);
            WriteAttribute(schema, 0, AttrCredential);
            WriteAttribute(schema, 1, AttrProfile);   // attributes[2].name stays NULL: the terminator
            _schema = schema;
            return true;
        }

        private static void WriteAttribute(IntPtr schema, int index, string name)
        {
            int offset = AttributeBase + index * AttributeSize;
            Marshal.WriteIntPtr(schema, offset, Marshal.StringToCoTaskMemUTF8(name));
            Marshal.WriteInt32(schema, offset + 8, SECRET_SCHEMA_ATTRIBUTE_STRING);
        }

        // GError is { GQuark domain; gint code; gchar *message; } — message at offset 8. Frees it.
        private static string? TakeError(IntPtr gerror)
        {
            if (gerror == IntPtr.Zero) return null;
            string message = Marshal.PtrToStringUTF8(Marshal.ReadIntPtr(gerror, 8)) ?? "unknown keyring error";
            g_error_free(gerror);
            return message;
        }

        /// <summary>The item's attributes as a GHashTable over UTF-8 strings owned here: the
        /// table is created without destroy functions, so the strings are freed after the call.</summary>
        private sealed class AttributeTable : IDisposable
        {
            public readonly IntPtr Handle;
            private readonly IntPtr[] _strings;

            public AttributeTable(string credential)
            {
                _strings = new[]
                {
                    Marshal.StringToCoTaskMemUTF8(AttrCredential), Marshal.StringToCoTaskMemUTF8(credential),
                    Marshal.StringToCoTaskMemUTF8(AttrProfile),    Marshal.StringToCoTaskMemUTF8(AppPaths.DataRoot),
                };
                Handle = g_hash_table_new(_strHash, _strEqual);
                g_hash_table_insert(Handle, _strings[0], _strings[1]);
                g_hash_table_insert(Handle, _strings[2], _strings[3]);
            }

            public void Dispose()
            {
                g_hash_table_unref(Handle);
                foreach (IntPtr s in _strings) Marshal.FreeCoTaskMem(s);
            }
        }
    }
}
