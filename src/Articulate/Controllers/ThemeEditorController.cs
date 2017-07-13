﻿using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Web;
using System.Web.Http;
using System.Web.Http.Controllers;
using System.Web.Http.Metadata;
using System.Web.Http.Routing;
using System.Web.Http.Validation;
using Articulate.Models;
using AutoMapper;
using Umbraco.Core;
using Umbraco.Core.IO;
using Umbraco.Core.Services;
using Umbraco.Web.Editors;
using Umbraco.Web.Models.ContentEditing;
using Umbraco.Web.Mvc;
using Umbraco.Web.WebApi;
using Umbraco.Web.WebApi.Filters;

namespace Articulate.Controllers
{

    [PluginController("Articulate")]    
    [UmbracoApplicationAuthorize(Constants.Applications.Settings)]
    public class ThemeEditorController : BackOfficeNotificationsController
    {
        private readonly IFileSystem2 _themesFileSystem = new PhysicalFileSystem(PathHelper.VirtualThemePath);

        public CodeFileDisplay PostCreateFile(string parentId, string name, string type)
        {
            //todo: what about paths? we need to know where we are
            if (string.IsNullOrWhiteSpace(parentId)) throw new ArgumentException("Value cannot be null or whitespace.", "parentId");
            if (string.IsNullOrWhiteSpace(name)) throw new ArgumentException("Value cannot be null or whitespace.", nameof(name));
            if (string.IsNullOrWhiteSpace(type)) throw new ArgumentException("Value cannot be null or whitespace.", nameof(type));

            // if the parentId is root (-1) then we just need an empty string as we are
            // creating the path below and we don't wan't -1 in the path
            if (parentId == Constants.System.Root.ToInvariantString())
            {
                parentId = string.Empty;
            }

            name = HttpUtility.UrlDecode(name);
            var virtualPath = name;
            if (parentId.IsNullOrWhiteSpace() == false)
            {
                parentId = HttpUtility.UrlDecode(parentId);
                virtualPath = parentId.EnsureEndsWith("/") + name;
            }

            var codeFile = new CodeFileDisplay
            {
                VirtualPath = virtualPath,
                Name = name
            };

            switch (type.ToLower())
            {
                case "javascript":
                    CreateOrUpdateFile(".js", codeFile);                    
                    break;
                case "css":
                    CreateOrUpdateFile(".css", codeFile);
                    break;
                case "razor":
                    CreateOrUpdateFile(".cshtml", codeFile);
                    break;
                case "folder":
                    virtualPath = NormalizeVirtualPath(virtualPath, PathHelper.VirtualThemePath);
                    _themesFileSystem.CreateFolder(virtualPath);

                    return new CodeFileDisplay
                    {
                        VirtualPath = virtualPath,
                        Path = Url.GetTreePathFromFilePath(virtualPath)                       
                    };
                default:
                    throw new HttpResponseException(Request.CreateResponse(HttpStatusCode.NotFound));
            }

            return MapFromVirtualPath(codeFile.VirtualPath);
        }


        private string NormalizeVirtualPath(string virtualPath, string systemDirectory)
        {
            if (virtualPath.IsNullOrWhiteSpace())
                return string.Empty;

            systemDirectory = systemDirectory.TrimStart("~");
            systemDirectory = systemDirectory.Replace('\\', '/');
            virtualPath = virtualPath.TrimStart("~");
            virtualPath = virtualPath.Replace('\\', '/');
            virtualPath = ClientDependency.Core.StringExtensions.ReplaceFirst(virtualPath, systemDirectory, string.Empty);

            return virtualPath;
        }

        public CodeFileDisplay PostSaveThemeFile(CodeFileDisplay themeFile)
        {
            if (themeFile == null) throw new ArgumentNullException("themeFile");

            if (ModelState.IsValid == false)
            {
                throw new HttpResponseException(Request.CreateErrorResponse(HttpStatusCode.BadRequest, ModelState));
            }

            switch (themeFile.FileType)
            {
                case "css":
                    CreateOrUpdateFile(".css", themeFile);
                    break;
                case "js":
                    CreateOrUpdateFile(".js", themeFile);
                    break;
                case "cshtml":
                    CreateOrUpdateFile(".cshtml", themeFile);
                    break;
                default:
                    throw new HttpResponseException(HttpStatusCode.NotFound);
            }

            return MapFromVirtualPath(themeFile.VirtualPath);
        }

        private void CreateOrUpdateFile(string expectedExtension, CodeFileDisplay display)
        {
            display.VirtualPath = EnsureCorrectFileExtension(NormalizeVirtualPath(display.VirtualPath, PathHelper.VirtualThemePath), expectedExtension);
            display.Name = EnsureCorrectFileExtension(display.Name, expectedExtension);

            //if the name has changed we need to delete and re-create
            if (!Path.GetFileNameWithoutExtension(display.VirtualPath).InvariantEquals(Path.GetFileNameWithoutExtension(display.Name)))
            {
                //remove the original file
                _themesFileSystem.DeleteFile(display.VirtualPath);
                //now update the virtual path to be correct
                var parts = display.VirtualPath.Split('/');
                display.VirtualPath = string.Join("/", parts.Take(parts.Length - 1)).EnsureEndsWith('/') + display.Name;
            }

            using (var stream = new MemoryStream())
            using (var writer = new StreamWriter(stream))
            {
                writer.Write(display.Content);

                writer.Flush();

                //create or overwrite it
                _themesFileSystem.AddFile(display.VirtualPath.TrimStart('/'), stream, true);
            }
        }

        private string EnsureCorrectFileExtension(string value, string extension)
        {
            if (value.EndsWith(extension) == false)
                value += extension;

            return value;
        }

        [HttpPost]
        [HttpDelete]
        public IHttpActionResult PostDeleteTheme(string themeName)
        {
            if (string.IsNullOrWhiteSpace(themeName))
                return NotFound();

            if (Path.GetInvalidFileNameChars().ContainsAny(themeName.ToCharArray()))
                return NotFound();

            var theme = new DirectoryInfo(Path.Combine(IOHelper.MapPath(PathHelper.VirtualThemePath))).GetDirectories()
                .FirstOrDefault(x => x.Name.InvariantEquals(themeName));

            if (theme == null)
                return NotFound();

            theme.Delete(true);

            return Ok();
        }


        public Theme PostCopyTheme(string themeName, string copy)
        {
            if (Path.GetInvalidFileNameChars().ContainsAny(themeName.ToCharArray()))
                throw new InvalidOperationException("Name cannot contain invalid file name characters");

            var sourceTheme = new DirectoryInfo(Path.Combine(IOHelper.MapPath(PathHelper.VirtualThemePath))).GetDirectories()
                .FirstOrDefault(x => x.Name.InvariantEquals(copy));
            if (sourceTheme == null) throw new HttpResponseException(HttpStatusCode.NotFound);

            var destTheme = new DirectoryInfo(Path.Combine(IOHelper.MapPath(PathHelper.VirtualThemePath))).GetDirectories()
                .FirstOrDefault(x => x.Name.InvariantEquals(themeName));
            if (destTheme != null)
            {
                ModelState.AddModelError("value", "The theme " + themeName + " already exists");
                throw new HttpResponseException(Request.CreateValidationErrorResponse(ModelState));
            }

            CopyDirectory(sourceTheme, new DirectoryInfo(Path.Combine(IOHelper.MapPath(PathHelper.VirtualThemePath), themeName)));

            return new Theme()
            {
                Name = themeName,
                Path = "-1," + themeName 
            };
        }

        private static void CopyDirectory(DirectoryInfo source, DirectoryInfo destination)
        {
            if (destination.Exists) throw new InvalidOperationException("Theme already exists");

            destination.Create();

            // Copy all files.
            FileInfo[] files = source.GetFiles();
            foreach (FileInfo file in files)
            {
                file.CopyTo(Path.Combine(destination.FullName,
                    file.Name));
            }

            // Process subdirectories.
            DirectoryInfo[] dirs = source.GetDirectories();
            foreach (DirectoryInfo dir in dirs)
            {
                // Get destination directory.
                string destinationDir = Path.Combine(destination.FullName, dir.Name);

                // Call CopyDirectory() recursively.
                CopyDirectory(dir, new DirectoryInfo(destinationDir));
            }
        }

        public IEnumerable<Theme> GetThemes()
        {
            var themes = new DirectoryInfo(Path.Combine(IOHelper.MapPath(PathHelper.VirtualThemePath))).GetDirectories()
                .Select(x => new Theme
                {
                    Name = x.Name
                });
            return themes;
        }

        public CodeFileDisplay GetByPath(string virtualPath)
        {
            if (string.IsNullOrWhiteSpace(virtualPath)) throw new ArgumentException("Value cannot be null or whitespace.", nameof(virtualPath));

            virtualPath = HttpUtility.UrlDecode(virtualPath);
            
            if (_themesFileSystem.FileExists(virtualPath))
            {
                return MapFromVirtualPath(virtualPath);
            }
            throw new HttpResponseException(HttpStatusCode.NotFound);
        }

        private CodeFileDisplay MapFromVirtualPath(string virtualPath)
        {
            using (var reader = new StreamReader(_themesFileSystem.OpenFile(virtualPath)))
            {
                var display = new CodeFileDisplay
                {
                    Content = reader.ReadToEnd(),
                    FileType = Path.GetExtension(virtualPath),
                    Id = HttpUtility.UrlEncode(virtualPath),
                    Name = Path.GetFileName(virtualPath),
                    Path = Url.GetTreePathFromFilePath(virtualPath),
                    VirtualPath = NormalizeVirtualPath(virtualPath, PathHelper.VirtualThemePath)
                };
                display.FileType = Path.GetExtension(virtualPath).TrimStart('.');
                return display;
            }
        }


    }
}