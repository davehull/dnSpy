﻿/*
    Copyright (C) 2014-2017 de4dot@gmail.com

    This file is part of dnSpy

    dnSpy is free software: you can redistribute it and/or modify
    it under the terms of the GNU General Public License as published by
    the Free Software Foundation, either version 3 of the License, or
    (at your option) any later version.

    dnSpy is distributed in the hope that it will be useful,
    but WITHOUT ANY WARRANTY; without even the implied warranty of
    MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.  See the
    GNU General Public License for more details.

    You should have received a copy of the GNU General Public License
    along with dnSpy.  If not, see <http://www.gnu.org/licenses/>.
*/

using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using dnlib.DotNet;
using dnlib.DotNet.MD;
using dnlib.PE;
using dnSpy.Contracts.Debugger;
using dnSpy.Contracts.Debugger.DotNet.Metadata.Internal;
using dnSpy.Contracts.Debugger.Engine;
using dnSpy.Debugger.DotNet.Metadata;
using Mono.Debugger.Soft;

namespace dnSpy.Debugger.DotNet.Mono.Impl {
	struct ModuleCreator {
		readonly DbgEngineImpl engine;
		readonly DbgObjectFactory objectFactory;
		readonly DbgAppDomain appDomain;
		readonly ModuleMirror monoModule;
		readonly int moduleOrder;
		ulong moduleAddress;
		uint moduleSize;
		bool isDynamic;
		bool isInMemory;

		ModuleCreator(DbgEngineImpl engine, DbgObjectFactory objectFactory, DbgAppDomain appDomain, ModuleMirror monoModule, int moduleOrder) {
			this.engine = engine;
			this.objectFactory = objectFactory;
			this.appDomain = appDomain;
			this.monoModule = monoModule;
			this.moduleOrder = moduleOrder;
			moduleAddress = 0;
			moduleSize = 0;
			isDynamic = false;
			isInMemory = false;
		}

		public static DbgEngineModule CreateModule<T>(DbgEngineImpl engine, DbgObjectFactory objectFactory, DbgAppDomain appDomain, ModuleMirror monoModule, int moduleOrder, T data) where T : class =>
			new ModuleCreator(engine, objectFactory, appDomain, monoModule, moduleOrder).CreateModuleCore(data);

		DbgEngineModule CreateModuleCore<T>(T data) where T : class {
			//TODO: Use func-eval to get these values, or update debugger agent
			moduleAddress = 0;
			moduleSize = 0;
			if (File.Exists(monoModule.FullyQualifiedName)) {
				isDynamic = false;
				isInMemory = false;
			}
			else {
				isDynamic = false;//TODO:
				isInMemory = true;
			}

			var imageLayout = CalculateImageLayout();
			string name = GetFilename(monoModule.FullyQualifiedName);
			string filename = monoModule.FullyQualifiedName;
			bool? isOptimized = CalculateIsOptimized();
			InitializeExeFields(filename, imageLayout, out var isExe, out var isDll, out var timestamp, out var version, out var assemblySimpleName);

			var reflectionAppDomain = ((DbgMonoDebugInternalAppDomainImpl)appDomain.InternalAppDomain).ReflectionAppDomain;

			var closedListenerCollection = new ClosedListenerCollection();
			var getMetadata = CreateGetMetadataDelegate(closedListenerCollection, imageLayout);
			var fullyQualifiedName = DmdModule.GetFullyQualifiedName(isInMemory, isDynamic, monoModule.FullyQualifiedName);
			DmdAssembly reflectionAssembly;
			DmdModule reflectionModule;
			if (monoModule == monoModule.Assembly.ManifestModule) {
				var assemblyLocation = isInMemory || isDynamic ? string.Empty : monoModule.FullyQualifiedName;
				var asmOptions = DmdCreateAssemblyOptions.None;
				if (isInMemory)
					asmOptions |= DmdCreateAssemblyOptions.InMemory;
				if (isDynamic)
					asmOptions |= DmdCreateAssemblyOptions.Dynamic;
				if (isExe)
					asmOptions |= DmdCreateAssemblyOptions.IsEXE;
				else if (isDll)
					asmOptions |= DmdCreateAssemblyOptions.IsDLL;
				var asmInfo = new DmdCreateAssemblyInfo(asmOptions, fullyQualifiedName, assemblyLocation, assemblySimpleName);
				reflectionAssembly = reflectionAppDomain.CreateAssembly(getMetadata, asmInfo);
				reflectionModule = reflectionAssembly.ManifestModule;
			}
			else {
				var manifestModule = engine.TryGetModule(monoModule.Assembly.ManifestModule);
				if (manifestModule == null)
					throw new InvalidOperationException();
				reflectionAssembly = ((DbgMonoDebugInternalModuleImpl)manifestModule.InternalModule).ReflectionModule.Assembly;
				reflectionModule = reflectionAppDomain.CreateModule(reflectionAssembly, getMetadata, isInMemory, isDynamic, fullyQualifiedName);
			}

			var internalModule = new DbgMonoDebugInternalModuleImpl(reflectionModule, closedListenerCollection);
			return objectFactory.CreateModule(appDomain, internalModule, isExe, moduleAddress, moduleSize, imageLayout, name, filename, isDynamic, isInMemory, isOptimized, moduleOrder, timestamp, version, engine.GetMessageFlags(), data: data, onCreated: engineModule => internalModule.SetModule(engineModule.Module));
		}

		Func<DmdLazyMetadataBytes> CreateGetMetadataDelegate(ClosedListenerCollection closedListenerCollection, DbgImageLayout imageLayout) {
			if (isDynamic)
				return CreateDynamicGetMetadataDelegate();
			else
				return CreateNormalGetMetadataDelegate(closedListenerCollection, imageLayout);
		}

		Func<DmdLazyMetadataBytes> CreateDynamicGetMetadataDelegate() {
			Debug.Assert(isDynamic);
			return () => new DmdLazyMetadataBytesArray(Array.Empty<byte>(), false);//TODO:
		}

		Func<DmdLazyMetadataBytes> CreateNormalGetMetadataDelegate(ClosedListenerCollection closedListenerCollection, DbgImageLayout imageLayout) {
			Debug.Assert(!isDynamic);

			var moduleAddressTmp = moduleAddress;
			var moduleSizeTmp = moduleSize;
			var engine = this.engine;
			var runtime = objectFactory.Runtime;
			var filename = monoModule.FullyQualifiedName;
			return () => {
				DbgRawMetadata rawMd = null;
				try {
					if (moduleAddressTmp != 0 && moduleSizeTmp != 0)
						rawMd = engine.RawMetadataService.Create(runtime, imageLayout == DbgImageLayout.File, moduleAddressTmp, (int)moduleSizeTmp);
					else if (File.Exists(filename))
						rawMd = engine.RawMetadataService.Create(runtime, imageLayout == DbgImageLayout.File, File.ReadAllBytes(filename));
					else {
						//TODO:
						rawMd = engine.RawMetadataService.Create(runtime, imageLayout == DbgImageLayout.File, Array.Empty<byte>());
					}
					closedListenerCollection.Closed += (s, e) => rawMd.Release();
					return new DmdLazyMetadataBytesPtr(rawMd.Address, (uint)rawMd.Size, rawMd.IsFileLayout);
				}
				catch {
					rawMd?.Release();
					throw;
				}
			};
		}

		DbgImageLayout CalculateImageLayout() {
			if (isDynamic)
				return DbgImageLayout.Unknown;
			if (isInMemory)
				return DbgImageLayout.File;
			return DbgImageLayout.Memory;
		}

		bool? CalculateIsOptimized() {
			return null;//TODO:
		}

		static string GetFilename(string s) {
			try {
				return Path.GetFileName(s);
			}
			catch {
			}
			return s;
		}

		void InitializeExeFields(string filename, DbgImageLayout imageLayout, out bool isExe, out bool isDll, out DateTime? timestamp, out string version, out string assemblySimpleName) {
			isExe = false;
			isDll = false;
			timestamp = null;
			version = null;
			assemblySimpleName = null;

			if (isDynamic) {
				if (monoModule.Assembly.ManifestModule == monoModule)
					version = monoModule.Assembly.GetName().Version.ToString();
			}
			else if (isInMemory) {
				Debug.Assert(imageLayout == DbgImageLayout.File, nameof(GetFileVersion) + " assumes file layout");

				var bytes = engine.DbgRuntime.Process.ReadMemory(moduleAddress, (int)moduleSize);
				if (bytes != null) {
					try {
						version = GetFileVersion(bytes);
						using (var peImage = new PEImage(bytes, imageLayout == DbgImageLayout.File ? ImageLayout.File : ImageLayout.Memory, true))
							InitializeExeFieldsFrom(peImage, out isExe, out isDll, out timestamp, ref version, out assemblySimpleName);
					}
					catch {
					}
				}
			}
			else {
				try {
					version = GetFileVersion(filename);
					using (var peImage = new PEImage(filename))
						InitializeExeFieldsFrom(peImage, out isExe, out isDll, out timestamp, ref version, out assemblySimpleName);
				}
				catch {
				}
			}

			if (version == null)
				version = string.Empty;
		}

		static void InitializeExeFieldsFrom(IPEImage peImage, out bool isExe, out bool isDll, out DateTime? timestamp, ref string version, out string assemblySimpleName) {
			isExe = (peImage.ImageNTHeaders.FileHeader.Characteristics & Characteristics.Dll) == 0;
			isDll = !isExe;

			// Roslyn sets bit 31 if /deterministic is used (the low 31 bits is not a timestamp)
			if (peImage.ImageNTHeaders.FileHeader.TimeDateStamp < 0x80000000)
				timestamp = Epoch.AddSeconds(peImage.ImageNTHeaders.FileHeader.TimeDateStamp);
			else
				timestamp = null;

			try {
				if (string.IsNullOrEmpty(version)) {
					using (var mod = ModuleDefMD.Load(peImage)) {
						if (string.IsNullOrEmpty(version))
							version = mod.Assembly?.Version.ToString();
						assemblySimpleName = UTF8String.ToSystemString(mod.Assembly?.Name);
					}
				}
				else {
					using (var md = MetaDataCreator.CreateMetaData(peImage)) {
						var row = md.TablesStream.ReadAssemblyRow(1);
						if (row == null)
							assemblySimpleName = null;
						else
							assemblySimpleName = md.StringsStream.Read(row.Name);
					}

				}
			}
			catch {
				assemblySimpleName = null;
			}
		}
		static readonly DateTime Epoch = new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc);

		static string GetFileVersion(string filename) {
			if (!File.Exists(filename))
				return string.Empty;
			try {
				var info = FileVersionInfo.GetVersionInfo(filename);
				return info.FileVersion ?? string.Empty;
			}
			catch {
			}
			return string.Empty;
		}

		static string GetFileVersion(byte[] bytes) {
			string tempFilename = null;
			try {
				tempFilename = Path.GetTempFileName();
				File.WriteAllBytes(tempFilename, bytes);
				return GetFileVersion(tempFilename);
			}
			catch {
			}
			finally {
				try {
					if (tempFilename != null)
						File.Delete(tempFilename);
				}
				catch { }
			}
			return string.Empty;
		}
	}
}
