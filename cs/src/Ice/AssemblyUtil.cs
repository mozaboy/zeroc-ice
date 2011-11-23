// **********************************************************************
//
// Copyright (c) 2003-2011 ZeroC, Inc. All rights reserved.
//
// This copy of Ice is licensed to you under the terms described in the
// ICE_LICENSE file included in this distribution.
//
// **********************************************************************

namespace IceInternal
{

    using System;
    using System.Collections;
    using System.Collections.Generic;
    using System.Diagnostics;
    using System.Reflection;
    using System.Threading;

    public sealed class AssemblyUtil
    {
        public enum Runtime { DotNET, Mono };
        public enum Platform { Windows, NonWindows };

        static AssemblyUtil()
        {
            PlatformID id = Environment.OSVersion.Platform;
            if(   id == PlatformID.Win32NT
               || id == PlatformID.Win32S
               || id == PlatformID.Win32Windows
               || id == PlatformID.WinCE)
            {
                platform_ = Platform.Windows;
            }
            else
            {
                platform_ = Platform.NonWindows;
            }

            if(System.Type.GetType("Mono.Runtime") != null)
            {
                runtime_ = Runtime.Mono;
            }
            else
            {
                runtime_ = Runtime.DotNET;
            }

            System.Version v = System.Environment.Version;
            runtimeMajor_ = v.Major;
            runtimeMinor_ = v.Minor;
            runtimeBuild_ = v.Build;
            runtimeRevision_ = v.Revision;

            v = System.Environment.OSVersion.Version;
            xp_ = v.Major == 5 && v.Minor == 1; // Are we running on XP?

            osx_ = false;

#if COMPACT
            //
            // Populate the _iceAssemblies list with the fully-qualified names
            // of the standard Ice assemblies. The fully-qualified name looks
            // like this:
            //
            // Ice, Version=X.Y.Z.0, Culture=neutral, PublicKeyToken=...
            //
            string name = Assembly.GetExecutingAssembly().FullName;
            _iceAssemblies.Add(name);
            int pos = name.IndexOf(',');
            if(pos >= 0 && pos < name.Length - 1)
            {
                //
                // Strip off the leading assembly name and use the remainder of the
                // string to compose the names of the other standard assemblies.
                //
                string suffix = name.Substring(pos + 1);
                _iceAssemblies.Add("Glacier2," + suffix);
                _iceAssemblies.Add("IceBox," + suffix);
                _iceAssemblies.Add("IceGrid," + suffix);
                _iceAssemblies.Add("IcePatch2," + suffix);
                _iceAssemblies.Add("IceStorm," + suffix);
            }
#else
            if(platform_ == Platform.NonWindows)
            {
                try
                {
                    Assembly a = Assembly.Load(
                        "Mono.Posix, Version=2.0.0.0, Culture=neutral, PublicKeyToken=0738eb9f132ed756");
                    Type syscall = a.GetType("Mono.Unix.Native.Syscall");
                    if(syscall != null)
                    {
                        MethodInfo method = syscall.GetMethod("uname", BindingFlags.Static | BindingFlags.Public);
                        if(method != null)
                        {
                            object[] p = new object[1];
                            method.Invoke(null, p);
                            if(p[0] != null)
                            {
                                Type utsname = a.GetType("Mono.Unix.Native.Utsname");
                                osx_ = ((string)utsname.GetField("sysname").GetValue(p[0])).Equals("Darwin");
                            }
                        }
                    }
                }
                catch(System.Exception)
                {
                }
            }
#endif
        }

        public static Type findType(Instance instance, string csharpId)
        {
#if !COMPACT
            loadAssemblies(); // Lazy initialization
#endif

            lock(_mutex)
            {
                Type t = (Type)_typeTable[csharpId];
                if(t != null)
                {
                    return t;
                }
#if COMPACT
                string[] assemblies = instance.factoryAssemblies();
                for(int i = 0; i < assemblies.Length; ++i)
                {
                    string s = csharpId + "," + assemblies[i];
                    if((t = Type.GetType(s)) != null)
                    {
                        _typeTable[csharpId] = t;
                        return t;
                    }
                }
                //
                // As a last resort, look for the type in the standard Ice assemblies.
                // This avoids the need for a program to set a property such as:
                //
                // Ice.FactoryAssemblies=Ice
                //
                foreach(string a in _iceAssemblies)
                {
                    string s = csharpId + "," + a;
                    if((t = Type.GetType(s)) != null)
                    {
                        _typeTable[csharpId] = t;
                        return t;
                    }
                }
#else
                foreach(Assembly a in _loadedAssemblies.Values)
                {
                    if((t = a.GetType(csharpId)) != null)
                    {
                        _typeTable[csharpId] = t;
                        return t;
                    }
                }
#endif
            }
            return null;
        }

#if !COMPACT
        public static Type[] findTypesWithPrefix(string prefix)
        {
            IceUtilInternal.LinkedList l = new IceUtilInternal.LinkedList();

            loadAssemblies(); // Lazy initialization

            lock(_mutex)
            {
                foreach(Assembly a in _loadedAssemblies.Values)
                {
                    Type[] types = a.GetTypes();
                    foreach(Type t in types)
                    {
                        if(t.AssemblyQualifiedName.IndexOf(prefix, StringComparison.Ordinal) == 0)
                        {
                            l.Add(t);
                        }
                    }
                }
            }

            Type[] result = new Type[l.Count];
            if(l.Count > 0)
            {
                l.CopyTo(result, 0);
            }
            return result;
        }
#endif

        public static object createInstance(Type t)
        {
            ConstructorInfo[] constructors = t.GetConstructors();

            if(constructors.Length == 0)
            {
                return null;
            }

            ParameterInfo[] firstConstructor = constructors[0].GetParameters();

            int paramCount = firstConstructor.Length;
            Type[] constructor = new Type[paramCount];
            for(int i = 0; i < paramCount; i++)
            {
                constructor[i] = firstConstructor[i].ParameterType;
            }

            return t.GetConstructor(constructor).Invoke(new object[]{});
        }

#if !COMPACT
        //
        // Make sure that all assemblies that are referenced by this process
        // are actually loaded. This is necessary so we can use reflection
        // on any type in any assembly because the type we are after will
        // most likely not be in the current assembly and, worse, may be
        // in an assembly that has not been loaded yet. (Type.GetType()
        // is no good because it looks only in the calling object's assembly
        // and mscorlib.dll.)
        //
        private static void loadAssemblies()
        {
            lock(_mutex)
            {
                if(!_assembliesLoaded)
                {
                    Assembly[] assemblies = AppDomain.CurrentDomain.GetAssemblies();
                    foreach(Assembly a in assemblies)
                    {
                        _loadedAssemblies[a.FullName] = a;
                    }
                    foreach(Assembly a in assemblies)
                    {
                        loadReferencedAssemblies(a);
                    }
                    _assembliesLoaded = true;
                }
            }
        }

        private static void loadReferencedAssemblies(Assembly a)
        {
            AssemblyName[] names = a.GetReferencedAssemblies();
            foreach(AssemblyName name in names)
            {
                if(!_loadedAssemblies.Contains(name.FullName))
                {
                    try
                    {
                        Assembly ra = Assembly.Load(name);
                        _loadedAssemblies[ra.FullName] = ra;
                        loadReferencedAssemblies(ra);
                    }
                    catch(System.Exception)
                    {
                        // Ignore assemblies that cannot be loaded.
                    }
                }
            }
        }

        private static bool _assembliesLoaded = false;
        private static Hashtable _loadedAssemblies = new Hashtable(); // <string, Assembly> pairs.
#else
        private static List<string> _iceAssemblies = new List<string>();
#endif
        private static Hashtable _typeTable = new Hashtable(); // <type name, Type> pairs.
        private static Mutex _mutex = new Mutex();

        public readonly static Runtime runtime_; // Either DotNET or Mono
        //
        // Versioning is: Major.Minor.Build.Revision. (Yes, really. It is not Major.Minor.Revision.Build, as
        // one might expect.) If a part of a version number (such as revision) is not defined, it is -1.
        //
        public readonly static int runtimeMajor_;
        public readonly static int runtimeMinor_;
        public readonly static int runtimeBuild_;
        public readonly static int runtimeRevision_;

        public readonly static Platform platform_;
        public readonly static bool xp_;
        public readonly static bool osx_;
    }

}
