using System;
using System.Collections.Generic;
using System.Xml;
#if CONTRACTS_FULL_SHIM
using Contract = System.Diagnostics.ContractsShim.Contract;
#else
using Contract = System.Diagnostics.Contracts.Contract; // SHIM'D
#endif

namespace KSoft.Phoenix.Xmb
{
	/*public */sealed class XmbFileContext
	{
		public Shell.ProcessorSize PointerSize;

		public bool CallOnRawDataRead;
	};

	/*public*/ sealed partial class XmbFile
		: IO.IEndianStreamable
		, IDisposable
	{
		public const string kFileExt = ".xmb";
		const uint kSignature = 0x71439800;

		List<Element> mElements;
		XmbVariantMemoryPool mPool;
		bool mHasUnicodeStrings;

		public bool HasUnicodeStrings => mHasUnicodeStrings;

		/// <summary>#HACK only valid during reading and when asked for</summary>
		internal Dictionary<uint, XmbVariant> mRawDataToSingle24Hack;

		Element NewElement(int rootElementIndex = TypeExtensions.kNone)
		{
			var e = new Element
			{
				Index = mElements.Count,
				RootElementIndex = rootElementIndex
			};

			mElements.Add(e);
			return e;
		}

		#region IDisposable Members
		public void Dispose()
		{
			if (mElements != null)
			{
				mElements.Clear();
				mElements = null;
			}

			Util.DisposeAndNull(ref mPool);
		}
		#endregion

		#region IEndianStreamable Members
		public void Read(IO.EndianReader s)
		{
			var context = KSoft.Debug.TypeCheck.CastReference<XmbFileContext>(s.UserData);

			using (s.ReadSignatureWithByteSwapSupport(kSignature))
			{
				if (context.PointerSize == Shell.ProcessorSize.x64)
				{
					// #HACK to deal with xmb files which weren't updated with new tools
					if (s.ByteOrder == Shell.EndianFormat.Big)
					{
						context.PointerSize = DetectBigEndianPointerSize(s);
					}
				}

				s.VirtualAddressTranslationInitialize(context.PointerSize);

				Values.PtrHandle elements_offset_pos;

				if (context.PointerSize == Shell.ProcessorSize.x64)
				{
					s.Pad32();
				}
				#region Initialize elements
				{
					int count = s.ReadInt32();
					if (context.PointerSize == Shell.ProcessorSize.x64)
					{
						s.Pad32();
					}
					s.ReadVirtualAddress(out elements_offset_pos);

					mElements = new List<Element>(count);
				}
				#endregion
				#region Initialize and read pool
				{
					int size = s.ReadInt32();
					if (context.PointerSize == Shell.ProcessorSize.x64)
					{
						s.Pad32();
					}
					Values.PtrHandle pool_offset_pos = s.ReadVirtualAddress();

					s.Seek((long)pool_offset_pos);
					byte[] buffer = s.ReadBytes(size);

					mPool = new XmbVariantMemoryPool(buffer, s.ByteOrder);
				}
				#endregion

				if (context.PointerSize == Shell.ProcessorSize.x64)
				{
					s.Pad64();
				}

				s.Seek((long)elements_offset_pos);
				for (int x = 0; x < mElements.Capacity; x++)
				{
					var e = new XmbFile.Element();
					mElements.Add(e);

					e.Index = x;
					e.Read(this, context, s);
				}

				foreach (XmbFile.Element e in mElements)
				{
					e.ReadAttributes(this, context, s);
					e.ReadChildren(this, context, s);
				}
			}
		}

		private static Shell.ProcessorSize DetectBigEndianPointerSize(IO.EndianReader s)
		{
			if (!s.BaseStream.CanSeek)
			{
				return Shell.ProcessorSize.x64;
			}

			long headerStart = s.BaseStream.Position;
			long streamLength = s.BaseStream.Length;

			var x32 = ReadHeaderInfo(s, headerStart, streamLength, Shell.ProcessorSize.x32);
			var x64 = ReadHeaderInfo(s, headerStart, streamLength, Shell.ProcessorSize.x64);

			s.Seek(headerStart);

			bool x32Plausible = IsHeaderPlausible(x32, streamLength, Shell.ProcessorSize.x32);
			bool x64Plausible = IsHeaderPlausible(x64, streamLength, Shell.ProcessorSize.x64);

			if (x64Plausible && (!x32Plausible || (x32.ElementCount == 0 && x64.ElementCount > 0)))
			{
				return Shell.ProcessorSize.x64;
			}

			if (x32Plausible)
			{
				return Shell.ProcessorSize.x32;
			}

			return Shell.ProcessorSize.x64;
		}

		private readonly struct HeaderInfo
		{
			public HeaderInfo(int elementCount, ulong elementsOffset, int poolSize, ulong poolOffset)
			{
				ElementCount = elementCount;
				ElementsOffset = elementsOffset;
				PoolSize = poolSize;
				PoolOffset = poolOffset;
			}

			public readonly int ElementCount;
			public readonly ulong ElementsOffset;
			public readonly int PoolSize;
			public readonly ulong PoolOffset;

			public static HeaderInfo Invalid => new(TypeExtensions.kNone, 0, TypeExtensions.kNone, 0);
		}

		private static HeaderInfo ReadHeaderInfo(IO.EndianReader s, long headerStart, long streamLength, Shell.ProcessorSize pointerSize)
		{
			if (pointerSize == Shell.ProcessorSize.x64)
			{
				if (streamLength < headerStart + 36)
				{
					return HeaderInfo.Invalid;
				}

				s.Seek(headerStart + sizeof(uint));
				int elementCount = s.ReadInt32();
				s.Seek(headerStart + 12);
				ulong elementsOffset = s.ReadUInt64();
				s.Seek(headerStart + 20);
				int poolSize = s.ReadInt32();
				s.Seek(headerStart + 28);
				ulong poolOffset = s.ReadUInt64();

				return new HeaderInfo(elementCount, elementsOffset, poolSize, poolOffset);
			}
			else
			{
				if (streamLength < headerStart + 16)
				{
					return HeaderInfo.Invalid;
				}

				s.Seek(headerStart);
				int elementCount = s.ReadInt32();
				ulong elementsOffset = s.ReadUInt32();
				int poolSize = s.ReadInt32();
				ulong poolOffset = s.ReadUInt32();

				return new HeaderInfo(elementCount, elementsOffset, poolSize, poolOffset);
			}
		}

		private static bool IsHeaderPlausible(HeaderInfo header, long streamLength, Shell.ProcessorSize pointerSize)
		{
			const int x32HeaderSize = 20;
			const int x64HeaderSize = 40;
			const int x32ElementSize = 28;
			const int x64ElementSize = 48;

			if (header.ElementCount < 0 || header.PoolSize < 0)
			{
				return false;
			}

			long minimumHeaderSize = pointerSize == Shell.ProcessorSize.x64
				? x64HeaderSize
				: x32HeaderSize;
			long elementSize = pointerSize == Shell.ProcessorSize.x64
				? x64ElementSize
				: x32ElementSize;

			if (streamLength < minimumHeaderSize)
			{
				return false;
			}

			if (header.ElementCount > 0)
			{
				if (header.ElementsOffset > long.MaxValue)
				{
					return false;
				}

				long elementsOffset = (long)header.ElementsOffset;
				long elementsSize = header.ElementCount * elementSize;

				if (elementsSize > streamLength ||
					elementsOffset < minimumHeaderSize ||
					elementsOffset > streamLength - elementsSize)
				{
					return false;
				}
			}

			if (header.PoolSize > 0)
			{
				if (header.PoolOffset > long.MaxValue)
				{
					return false;
				}

				long poolOffset = (long)header.PoolOffset;

				if (header.PoolSize > streamLength ||
					poolOffset < minimumHeaderSize ||
					poolOffset > streamLength - header.PoolSize)
				{
					return false;
				}
			}

			return true;
		}

		public void Write(IO.EndianWriter s)
		{
			var context = KSoft.Debug.TypeCheck.CastReference<XmbFileContext>(s.UserData);

			s.Write(kSignature);
			if (context.PointerSize == Shell.ProcessorSize.x64)
			{
				s.Pad32();
			}

			#region Elements header
			s.Write(mElements.Count);
			if (context.PointerSize == Shell.ProcessorSize.x64)
			{
				s.Pad32();
			}
			var elements_offset_pos = s.MarkVirtualAddress(context.PointerSize);
			#endregion

			#region Pool header
			s.Write(mPool.Size);
			if (context.PointerSize == Shell.ProcessorSize.x64)
			{
				s.Pad32();
			}
			var pool_offset_pos = s.MarkVirtualAddress(context.PointerSize);
			#endregion

			if (context.PointerSize == Shell.ProcessorSize.x64)
			{
				s.Pad64();
			}

			var elements_offset = s.PositionPtr;
			foreach (var e in mElements)
			{
				e.Write(s);
			}
			foreach (var e in mElements)
			{
				e.WriteAttributes(s);
				e.WriteChildren(s);
			}

			var pool_offset = s.PositionPtr;
			mPool.Write(s);

			s.Seek((long)elements_offset_pos);
			s.WriteVirtualAddress(elements_offset);
			s.Seek((long)pool_offset_pos);
			s.WriteVirtualAddress(pool_offset);
		}
		#endregion

		string ToString(XmbVariant v) => v.ToString(mPool);

		public XmlDocument ToXmlDocument()
		{
			Contract.Ensures(Contract.Result<XmlDocument>() != null);

			var doc = new XmlDocument();

			return ToXmlDocument(doc);
		}

		public XmlDocument ToXmlDocument(XmlDocument doc)
		{
			Contract.Ensures(doc == null || Contract.Result<XmlDocument>() != null);

			if (doc != null && mElements != null && mElements.Count > 0)
			{
				XmbFile.Element root = mElements[0];
				var root_e = root.ToXml(this, doc, null);

				doc.AppendChild(root_e);
			}

			return doc;
		}

		#region FromXml
		public void FromXml(XmlElement /*root*/_)
		{
			//var e = new Element();

			// #TODO
		}
		#endregion
		#region ToXml
		public void ToXml(string file)
		{
			Contract.Requires(!string.IsNullOrEmpty(file));

			using (var fs = System.IO.File.Create(file))
			{
				ToXml(fs);
			}
		}
		public void ToXml(System.IO.Stream stream)
		{
			Contract.Requires(stream != null);

			var doc = ToXmlDocument();

			var encoding = mHasUnicodeStrings
				? System.Text.Encoding.UTF8
				: System.Text.Encoding.ASCII;
			var xml_writer_settings = new XmlWriterSettings()
			{
				Indent = true,
				IndentChars = "\t",
				CloseOutput = false,
				Encoding = encoding,
			};
			using (var xml = XmlWriter.Create(stream, xml_writer_settings))
			{
				doc.Save(xml);
			}
		}
		#endregion

		// #HACK
		private void OnRawDataRead(XmbFile.Element element, uint rawData, XmbVariant variant)
		{
			Util.MarkUnusedVariable(ref element);

			XmbVariantSerialization.RawVariantType rawVariantType = XmbVariantSerialization.GetTypeFromRawData(rawData);

			if (rawVariantType == XmbVariantSerialization.RawVariantType.Single24)
			{
				if (mRawDataToSingle24Hack == null)
				{
					mRawDataToSingle24Hack = new Dictionary<uint, XmbVariant>();
				}

				mRawDataToSingle24Hack[rawData] = variant;
			}
		}

		public bool DumpSingle24Values(Xmb.Single24DumpInfo dumpInfo)
		{
			ArgumentNullException.ThrowIfNull(dumpInfo);

			if (mRawDataToSingle24Hack != null && mRawDataToSingle24Hack.Count > 1)
			{
				foreach (KeyValuePair<uint, XmbVariant> kvp in mRawDataToSingle24Hack)
				{
					uint rawDataValue = XmbVariantSerialization.GetValueFromRawData(kvp.Key);
					dumpInfo.AddEntry(rawDataValue, kvp.Value.Single, null);
				}

				return true;
			}

			return false;
		}
	};
}
