using System;
using System.Buffers.Binary;
using System.IO;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace KSoft.Phoenix.Xmb.Test
{
	[TestClass]
	public sealed class XmbFileTests
		: BaseTestClass
	{
		[TestMethod]
		public void XmbFile_ReadBigEndianX64SingleElement_ToXml()
		{
			using var ms = new MemoryStream(CreateBigEndianX64SingleElementXmb());
			using var s = new IO.EndianReader(ms, Shell.EndianFormat.Big);
			s.UserData = new XmbFileContext
			{
				PointerSize = Shell.ProcessorSize.x64,
			};

			using var xmb = new XmbFile();
			xmb.Read(s);

			var doc = xmb.ToXmlDocument();

			Assert.IsNotNull(doc.DocumentElement);
			Assert.AreEqual("r", doc.DocumentElement.Name);
			Assert.AreEqual(0, doc.DocumentElement.ChildNodes.Count);
		}

		static byte[] CreateBigEndianX64SingleElementXmb()
		{
			const int headerSize = 40;
			const int elementSize = 48;
			const int elementOffset = headerSize;
			const int poolOffset = headerSize + elementSize;

			var data = new byte[poolOffset];

			WriteUInt32(data, 0, 0x71439800); // signature
			WriteInt32(data, 8, 1); // element count
			WriteUInt64(data, 16, elementOffset);
			WriteInt32(data, 24, 0); // pool size
			WriteUInt64(data, 32, poolOffset);

			WriteInt32(data, elementOffset + 0, TypeExtensions.kNone);
			WriteUInt32(data, elementOffset + 4, 0x08000072); // direct ANSI string "r"
			WriteUInt32(data, elementOffset + 8, 0); // no inner text
			WriteInt32(data, elementOffset + 16, 0); // attribute count
			WriteUInt64(data, elementOffset + 24, ulong.MaxValue);
			WriteInt32(data, elementOffset + 32, 0); // child count
			WriteUInt64(data, elementOffset + 40, ulong.MaxValue);

			return data;
		}

		static void WriteInt32(byte[] data, int offset, int value)
			=> BinaryPrimitives.WriteInt32BigEndian(data.AsSpan(offset, sizeof(int)), value);

		static void WriteUInt32(byte[] data, int offset, uint value)
			=> BinaryPrimitives.WriteUInt32BigEndian(data.AsSpan(offset, sizeof(uint)), value);

		static void WriteUInt64(byte[] data, int offset, ulong value)
			=> BinaryPrimitives.WriteUInt64BigEndian(data.AsSpan(offset, sizeof(ulong)), value);
	}
}
