// Copyright 2005 Rapha�l Slinckx <raphael@slinckx.net> 
//
// (see http://entagged.sourceforge.net)
//
// Licensed under the Apache License, Version 2.0 (the "License");
// you may not use this file except in compliance with the License.
// You may obtain a copy of the License at
//
// http://www.apache.org/licenses/LICENSE-2.0
//
// Unless required by applicable law or agreed to in writing, software
// distributed under the License is distributed on an "AS IS" BASIS, WITHOUT
// WARRANTIES OR CONDITIONS OF ANY KIND, either express or implied. See
// the License for the specific language governing permissions and
// limitations under the License.

/*
 * $Log$
 * Revision 1.1  2005/06/27 00:47:25  abock
 * Added entagged-sharp
 *
 * Revision 1.7  2005/02/22 22:21:17  kikidonk
 * Should fixes most times length/bitrate errors
 *
 * Revision 1.6  2005/02/21 00:13:00  kikidonk
 * Should fix the bitrate calculation for mp3
 *
 * Revision 1.5  2005/02/18 13:38:11  kikidonk
 * Adds a isVbr method that checks wether the file is vbr or not, added check in OGG and MP3, other formats are always VBR
 *
 * Revision 1.4  2005/02/08 12:54:41  kikidonk
 * Added cvs log and header
 *
 */

using System.IO;
using Entagged.Audioformats.exceptions;

namespace Entagged.Audioformats.Mp3.Util {
	public class Mp3InfoReader {
		public EncodingInfo Read( Stream raf ) {
			EncodingInfo encodingInfo = new EncodingInfo();
			
			//Begin info fetch-------------------------------------------
			if ( raf.Length == 0 ) {
				throw new CannotReadException("File is empty");
			}
			
			int id3TagSize = 0;
			raf.Seek( 0 , SeekOrigin.Begin);
		// skip id3v2 tag, because there may be long pictures inside with
		// slows reading and they can be not unsyncronized
			byte[] bbb = new byte[3];
				raf.Read(bbb, 0, 3);
				raf.Seek(0, SeekOrigin.Begin);
				string ID3 = new string(System.Text.Encoding.ASCII.GetChars(bbb));
				if (ID3 == "ID3") {
					raf.Seek(6, SeekOrigin.Begin);
					id3TagSize = ReadSyncsafeInteger(raf);
					raf.Seek(id3TagSize+10, SeekOrigin.Begin);
				}

			MPEGFrame firstFrame = null;
			
			byte[] b = new byte[4];
			raf.Read(b, 0, b.Length);
			
			// search for sync mark, but also for a right bitrate, samplerate and layer(that way you can
			// read corrupted but playable files)
			while ( !( (b[0]&0xFF)==0xFF  &&  (b[1]&0xE0)==0xE0 && (b[1]&0x06)!=0  && (b[2]&0xF0)!=0xF0  && (b[2]&0x0C)!=0x0C ) && raf.Position < raf.Length-4) {
				raf.Seek( -3, SeekOrigin.Current);
				raf.Read(b, 0, b.Length);
			}

			firstFrame = new MPEGFrame( b );
			if ( firstFrame == null || !firstFrame.Valid || firstFrame.SamplingRate == 0 ) {
				//MP3File corrupted, no valid MPEG frames
				throw new CannotReadException("Error: could not synchronize to first mp3 frame");
			}

			int firstFrameLength = firstFrame.FrameLength;
			//----------------------------------------------------------------------------
			int skippedLength = 0;

			if ( firstFrame.MpegVersion == MPEGFrame.MPEG_VERSION_1 && firstFrame.ChannelMode == MPEGFrame.CHANNEL_MODE_MONO ) {
				raf.Seek( 17, SeekOrigin.Current );
				skippedLength += 17;
			}
			else if ( firstFrame.MpegVersion == MPEGFrame.MPEG_VERSION_1 ) {
				raf.Seek( 32 , SeekOrigin.Current );
				skippedLength += 32;
			}
			else if ( firstFrame.MpegVersion == MPEGFrame.MPEG_VERSION_2 && firstFrame.ChannelMode == MPEGFrame.CHANNEL_MODE_MONO ) {
				raf.Seek(9, SeekOrigin.Current );
				skippedLength += 9;
			}
			else if ( firstFrame.MpegVersion == MPEGFrame.MPEG_VERSION_2 ) {
				raf.Seek( 17, SeekOrigin.Current );
				skippedLength += 17;
			}
			int optionalFrameLength = 0;

			byte[] xingPart1 = new byte[16];

			raf.Read( xingPart1, 0, xingPart1.Length );
			raf.Seek( 100 , SeekOrigin.Current );

			byte[] xingPart2 = new byte[4];

			raf.Read( xingPart2, 0, xingPart2.Length );

			XingMPEGFrame currentXingFrame = new XingMPEGFrame( xingPart1, xingPart2 );
			//System.err.println(currentXingFrame);

			if ( !currentXingFrame.Valid )
				raf.Seek( - 120 - skippedLength - 4 , SeekOrigin.Current );  //120 Xing bytes, unused skipped bytes and 4 mpeg info bytes
			//Skipping Xing frame reading

			else {
				optionalFrameLength += 120;

				byte[] lameHeader = new byte[36];

				raf.Read( lameHeader, 0,  lameHeader.Length);

				LameMPEGFrame currentLameFrame = new LameMPEGFrame( lameHeader );

				if ( !currentLameFrame.Valid )
					raf.Seek( - 36 , SeekOrigin.Current );
				//Skipping Lame frame reading

				else
					optionalFrameLength += 36;
				//Lame Frame read

			}
			//----------------------------------------------------------------------------
			//----------------------------------------------------------------------------
			//Length computation
			if ( currentXingFrame.Valid )
				raf.Seek( firstFrameLength - ( skippedLength + optionalFrameLength + 4 ) , SeekOrigin.Current );

			double timePerFrame = ((double) firstFrame.SampleNumber) / firstFrame.SamplingRate;
			double lengthInSeconds;
			if ( currentXingFrame.Valid ) {
			    //Preffered Method: extracts time length with the Xing Header (vbr:Xing or cbr:Info)********************
			    
			    lengthInSeconds = ( timePerFrame * currentXingFrame.FrameCount );
			    
			    encodingInfo.Vbr = currentXingFrame.IsVbr;
			    int fs = currentXingFrame.FileSize;
			    encodingInfo.Bitrate = (int)( ( (fs==0 ? raf.Length-id3TagSize : fs) * 8 ) / ( timePerFrame * currentXingFrame.FrameCount * 1000 ) );
			}
			else {
			    //Default Method: extracts time length using the file length and assuming CBR********************
			    int frameLength = firstFrame.FrameLength;
				if (frameLength==0)
					throw new CannotReadException("Error while reading header(maybe file is corrupted, or missing first mpeg frame before xing header)");
	
			    lengthInSeconds =  timePerFrame * ((raf.Length-id3TagSize) / frameLength);
			    
			    encodingInfo.Vbr = false;
			    encodingInfo.Bitrate =  firstFrame.Bitrate ;
			}
		
			//Populates encodingInfo----------------------------------------------------
			encodingInfo.Length = (int) lengthInSeconds;
			encodingInfo.ChannelNumber = firstFrame.ChannelNumber;
			encodingInfo.SamplingRate = firstFrame.SamplingRate;
			encodingInfo.EncodingType = firstFrame.MpegVersionToString( firstFrame.MpegVersion ) + " || " + firstFrame.LayerToString( firstFrame.LayerVersion );
			encodingInfo.ExtraEncodingInfos = "";
		
			return encodingInfo;
		}
		
		private int ReadSyncsafeInteger(Stream raf) {
			int value = 0;

			value += (raf.ReadByte()& 0xFF) << 21;
			value += (raf.ReadByte()& 0xFF) << 14;
			value += (raf.ReadByte()& 0xFF) << 7;
			value += (raf.ReadByte()& 0xFF);

			return value;
		}
	}
}
