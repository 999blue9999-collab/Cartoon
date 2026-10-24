provider "aws" {
  region = var.aws_region

  # Credentials are resolved via the standard AWS credential chain
  # (environment variables, shared config/credentials, instance profile, etc.).
  # Do NOT hardcode credentials here.

  default_tags {
    tags = {
      Project     = "golu-kal-ka-dost"
      ManagedBy   = "terraform"
      Environment = var.environment
    }
  }
}
