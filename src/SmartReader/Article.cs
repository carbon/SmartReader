﻿using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

using AngleSharp.Dom;

namespace SmartReader
{
    /// <summary>
    /// Parsed article
    /// </summary>
    /// <remarks>
    /// You should check the property <c>IsReadable</c> to know whether an article was actually found
    /// </remarks>
    public class Article
    {
        /// <value>The original URI of the source</value>
        public Uri Uri { get; private set; }
        /// <value>The clean title</value>
        public string Title { get; private set; }
        /// <value>The parsed byline</value>
        public string Byline { get; private set; }
        /// <value>The direction of the writing</value>
        public string Dir { get; private set; }
        /// <value>The URI of the main image</value>
        public string FeaturedImage { get; private set; }
        /// <value>The HTML content</value>
        public string Content { get; private set; }
        /// <value>The pure-text content cleaned to be readable</value>
        public string TextContent { get; private set; }
        /// <value>The excerpt provided by the metadata</value>
        public string Excerpt { get; private set; }
        /// <value>The language provided by the metadata</value>
        public string? Language { get; private set; }
        /// <value>The author, which can be parsed or read in the metadata</value>
        public string? Author { get; private set; }
        /// <value>The name of the website, which can be parsed or read in the metadata </value>
        public string? SiteName { get; private set; }
        /// <value>The length in bytes of <c>Content</c></value>
        public int Length { get; private set; }
        /// <value>The average time to read</value>
        /// <remarks>It is based on http://iovs.arvojournals.org/article.aspx?articleid=2166061</remarks>
        public TimeSpan TimeToRead { get; private set; }
        /// <value>The publication date, which can be parsed or read in the metadata</value>
        public DateTime? PublicationDate { get; private set; }
        /// <value>It indicates whether an article was actually found</value>
        public bool IsReadable { get; private set; }
        /// <summary>The function that will serialize the HTML content of the article</summary>
        /// <value>Default: return InnerHTML property</value>       
        public static Func<IElement, string> Serializer { get; set; } = new Func<IElement, string>((el) => { return el.InnerHtml; });

        /// <summary>The function that will extract the text from the HTML content</summary>
        /// <value>Default: return InnerHTML property</value>       
        public static Func<IElement, string> Converter { get; set; } = ConvertToPlaintext;

        private readonly IElement? _element = null;
        private readonly Reader? _reader;

        internal IElement? Element => _element;

        internal Article(Uri uri, string title, string byline, string dir, string? language, string? author, IElement element, Metadata metadata, bool readable, Reader reader)
        {
            _element = element;
            _reader = reader;

            Uri = uri;
            Title = title;
            Byline = string.IsNullOrWhiteSpace(byline) ? metadata.Author : byline;
            Dir = dir;
            Content = Serializer(element);
            TextContent = Converter(element);
            Excerpt = metadata.Excerpt;
            Length = element.TextContent.Length;
            Language = string.IsNullOrWhiteSpace(metadata.Language) ? language : metadata.Language;
            PublicationDate = metadata.PublicationDate;
            Author = string.IsNullOrWhiteSpace(metadata.Author) ? author : metadata.Author;
            SiteName = metadata.SiteName;
            IsReadable = readable;
            TimeToRead = TimeToReadCalculator.Calculate(this);
            FeaturedImage = metadata.FeaturedImage;          
        }

        /// <summary>
        /// The constructor used when we fail to find an actual article
        /// </summary>
        internal Article(Uri uri, string title, bool readable)
        {
            IsReadable = readable;
            Uri = uri;
            Title = title;
            Dir = "";
            Byline = "";
            Content = "";
            TextContent = "";
            Excerpt = "";
            Length = 0;
            Language = "";
            PublicationDate = new DateTime();
            Author = "";
            TimeToRead = new TimeSpan();
            FeaturedImage = "";
        }

        /// <summary>
        /// Finds images contained in the article.
        /// </summary>
        /// <param name="minSize">The minium size in bytes to be considered a image.</param>        
        /// <returns>
        /// A Task object with the images found
        /// </returns>  
        public async Task<IReadOnlyList<Image>> GetImagesAsync(long minSize = 75_000)
        {
            var imgs = _element?.QuerySelectorAll("img");

            if (imgs is null)
            {
                return Array.Empty<Image>();
            }

            var images = new List<Image>();

            foreach (var img in imgs)
            {
                if (img.GetAttribute("src") is { Length: > 0 } src)
                {
                    long size = 0;

                    var imageUri = new Uri(src);

                    try
                    {
                        imageUri = new Uri(Uri.ToAbsoluteURI(imageUri.ToString()));
                        size = await _reader!.GetImageSizeAsync(imageUri);
                    }
                    catch { }

                    string description = img.GetAttribute("alt");
                    string title = img.GetAttribute("title");

                    if (size > minSize)
                    {
                        images.Add(new Image()
                        {
                            Size = size,
                            Source = imageUri,
                            Description = description,
                            Title = title
                        });
                    }
                }
            }

            // if there is no featured image, let's set the first one we found
            if (string.IsNullOrEmpty(FeaturedImage) && images.Count > 0)
            {
                FeaturedImage = images[0].Source!.ToString();
            }

            return images;
        }

        /// <summary>
        /// Convert images contained in the article to their data URI scheme representation
        /// </summary>
        /// <param name="minSize">The minium size in bytes to be considered a image. Smaller images are removed</param>        
        /// <returns>
        /// An empty Task object
        /// </returns>  
        public async Task ConvertImagesToDataUriAsync(long minSize = 75000)
        {
            if (_element is null) return;

            foreach (var img in _element.QuerySelectorAll("img"))
            {
                if (img.GetAttribute("src") is string src)
                {
                    var imageUri = new Uri(src);

                    try
                    {
                        // download image
                        byte[] bytes = await _reader!.GetImageBytesAsync(imageUri).ConfigureAwait(false);

                        if (bytes.LongLength > minSize)
                        {
                            // convert it to data uri scheme and replace the original source
                            img.SetAttribute("src", Image.ConvertImageToDataUri(imageUri.AbsolutePath, bytes));
                        }
                        else
                        {
                            img.Remove();
                        }                            
                    }
                    catch { }                                                
                }
            }

            // we update the affected properties
            Content = _element.InnerHtml;            
        }

        /// <summary>
        /// Convert the article content from HTML to Text cleaning the results
        /// </summary>      
        /// <returns>
        /// A string representing the text
        /// </returns>  
        private static string ConvertToPlaintext(IElement doc)
        {
            var writer = new StringWriter();

            string text = ConvertToText(doc, writer);

            bool previousSpace = false;            
            bool previousNewline = false;
            int index = 0;

            // fix whitespace 
            // replace tabs with one space
            text = Regex.Replace(text, "\t+", " ");

            // replace multiple newlines with max two
            text = Regex.Replace(text, "(\\r?\\n){3,}", $"{writer.NewLine}{writer.NewLine}");

            var stringBuilder = new StringBuilder(text);

            while (index < stringBuilder.Length)
            {
                // carriage return and line feed are not separator characters
                bool isSpace = char.IsSeparator(stringBuilder[index]);              
                bool isNewline = stringBuilder[index] == '\r' || stringBuilder[index] == '\n';

                // we remove a space before a newline
                if (previousSpace && isNewline)
                    stringBuilder.Remove(index - 1, 1);
                // we remove a space after a newline
                else if (previousNewline && isSpace)
                    stringBuilder.Remove(index, 1);
                // we remove series of spaces
                else if (previousSpace && isSpace)
                    stringBuilder.Remove(index, 1);
                else
                    index++;

                previousSpace = isSpace;
                previousNewline = isNewline;                
            }

            // we trim all whitespace
            text = stringBuilder.ToString().Trim();

            // replace multiple newlines with max two
            text = Regex.Replace(text, "(\\r?\\n){3,}", $"{writer.NewLine}{writer.NewLine}");

            return text;
        }

        /// <summary>
        /// The function that converts HTML markup to text
        /// </summary>
        private static string ConvertToText(IElement doc, StringWriter text)
        {
            if (doc.NodeType == NodeType.Element && doc.NodeName is "P")
                text.Write(text.NewLine);
            else if (doc.NodeType == NodeType.Element && doc.NodeName is "BR")
                text.Write(text.NewLine);
            
            if (doc.HasChildNodes)
            {
                foreach (INode el in doc.ChildNodes)
                {
                    // if the element has other elements we look inside of them
                    if (el.NodeType == NodeType.Element
                        || el.NodeType == NodeType.EntityReference)
                        ConvertToText((IElement)el, text);
                    // if the element has children text nodes we extract the text
                    if (el.NodeType == NodeType.Text)
                        text.Write(el.TextContent);
                }
            }

            if (doc.NodeType == NodeType.Element && doc.NodeName is "P")
                text.Write(text.NewLine);

            return text.ToString();
        }
    }
}
